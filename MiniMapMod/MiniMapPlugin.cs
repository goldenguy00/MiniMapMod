using BepInEx;
using RoR2;
using UnityEngine;
using MiniMapLibrary;
using System.Collections.Generic;
using System.Linq;
using System;
using BepInEx.Configuration;
using MiniMapMod.Adapters;
using MiniMapLibrary.Config;
using MiniMapLibrary.Scanner;

namespace MiniMapMod
{
    [BepInPlugin("MiniMap", "Mini Map Mod", "3.3.2")]
    public class MiniMapPlugin : BaseUnityPlugin
    {
        public bool Enabled { get; private set; } = true;

        private IConfig config;
        private MiniMapLibrary.ILogger logger;

        private ISpriteManager spriteManager;

        private ITrackedObjectScanner[] staticScanners;
        private ITrackedObjectScanner[] dynamicScanners;

        private CameraRigController cameraRig;
        private Minimap minimap;

        private bool scannedStaticObjects = false;

        private readonly Timer dynamicScanTimer = new(5.0f);
        private readonly Timer cooldownTimer = new(2.0f, false) { Value = -1.0f };
        private readonly List<ITrackedObject> trackedObjects = [];
        private readonly Range3D trackedDimensions = new();

        private static GameObject DefaultConverter<T>(T value) where T : MonoBehaviour => value ? value.gameObject : null;
        private static bool DefaultSelector(object _) => true;

        public void Awake()
        {
            // wrap the bepinex logger with an adapter so 
            // we can pass it to the business layer
            logger = new Log(base.Logger);

            spriteManager = new SpriteManager(logger);

            // create the minimap controller
            minimap = new(logger);

            // SETUP CONFIG

            // wrap bepinex config so we can pass it to business layer
            config = new ConfigAdapter(this.Config);

            Settings.LoadApplicationSettings(logger, config);

            // bind options
            InteractableKind[] kinds = Enum.GetValues(typeof(InteractableKind)).Cast<InteractableKind>().Where(x => x != InteractableKind.none && x != InteractableKind.All).ToArray();

            foreach (var item in kinds)
            {
                Settings.LoadConfigEntries(item, config);
            }

            logger.LogInfo("Creating scene scan hooks");

            // fill the scanner arrays
            CreateStaticScanners();

            CreateDynamicScanners();

            // hook events so the minimaps updates
            // scan scene should NEVER throw exceptions
            // doing so prevents all other subscribing events to not fire (after the exception)

            // this will re-scan the scene every time any npc, player dies
            GlobalEventManager.onCharacterDeathGlobal += (x) => ScanScene();

            // this will re-scan when the player uses anything like a chest
            // or the landing pod
            GlobalEventManager.OnInteractionsGlobal += (x, y, z) => ScanScene();

            // update the minimap automatically every N seconds regardless of deaths etc
            dynamicScanTimer.OnFinished += (x) => ScanScene();
        }

        //The Update() method is run on every frame of the game.
        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(Settings.MinimapKey))
            {
                Enabled = !Enabled;

                if (Enabled == false)
                {
                    logger.LogInfo("Resetting minimap");
                    Reset();
                    return;
                }
            }

            cooldownTimer.Update(Time.deltaTime);
            dynamicScanTimer.Update(Time.deltaTime);

            if (Enabled)
            {
                // the main camera becomes null when the scene ends on death or quits
                if (Camera.main == null)
                {
                    logger.LogDebug("Main camera was null, resetting minimap");
                    Reset();
                    return;
                }

                if (minimap.Created)
                {
                    //try
                    //{
                    minimap.SetRotation(Camera.main.transform.rotation);

                    UpdateIconPositions();

                    if (Input.GetKeyDown(Settings.MinimapIncreaseScaleKey))
                    {
                        Minimap.Container.transform.localScale *= 1.1f;
                    }
                    else if (Input.GetKeyDown(Settings.MinimapDecreaseScaleKey))
                    {
                        Minimap.Container.transform.localScale *= 0.90f;
                    }
                    /*}
                    catch (NullReferenceException)
                    {
                        // we'll encounter null references when other mods or the game itself
                        // destroys entities we are tracking at runtime
                        logger.LogDebug($"{nameof(NullReferenceException)} was encountered while updating positions, reseting minimap");
                        Reset();
                    }*/
                }
                else
                {
                    if (TryCreateMinimap())
                    {
                        trackedDimensions.Clear();

                        ScanScene();
                    }
                }
            }
        }

        private void UpdateIconPositions()
        {
            // only perform this calculation once per frame
            Vector2 cameraPositionMinimap = GetPlayerPosition().ToMinimapPosition(trackedDimensions);

            for (int i = 0; i < trackedObjects.Count; i++)
            {
                ITrackedObject item = trackedObjects[i];

                // if we dont have a reference to the gameobject any more, remove it and continue
                if (item.gameObject == null)
                {
                    trackedObjects.RemoveAt(i);

                    // if we still have a icon for the now discarded item destroy it
                    if (item.MinimapTransform != null)
                    {
                        GameObject.Destroy(item.MinimapTransform.gameObject);
                    }

                    continue;
                }

                // convert the world positions to minimap positions
                // remember the minimap is calculated on a scale from 0d to 1d where 0d is the least most coord of any interactible and 1d is the largest coord of any interactible

                Vector2 itemMinimapPosition = item.gameObject.transform.position.ToMinimapPosition(trackedDimensions) - cameraPositionMinimap;

                // there exists no icon when .MinimapTransform is null
                if (item.MinimapTransform == null)
                {
                    // create one
                    item.MinimapTransform = minimap.CreateIcon(item.InteractableType, itemMinimapPosition, this.spriteManager);
                }
                else
                {
                    // since it was already created update the position
                    item.MinimapTransform.localPosition = itemMinimapPosition;

                    // becuase we don't want the icons to spin WITH the minimap set their rotation
                    // every frame so they're always facing right-side up
                    // (they inherit their position from the minimap)
                    item.MinimapTransform.rotation = Quaternion.identity;
                }

                // check to see if its active and whether to change its color
                // check the position to see if we should show arrow
                item.CheckActive();
            }
        }

        private bool TryCreateMinimap()
        {
            GameObject objectivePanel = GameObject.Find("ObjectivePanel");

            if (objectivePanel == null || this.spriteManager == null)
            {
                minimap.Destroy();
                return false;
            }

            logger.LogInfo("Creating Minimap object");

            minimap.CreateMinimap(this.spriteManager, objectivePanel.gameObject);

            Minimap.Container.transform.localScale = Vector3.one * Settings.MinimapScale;

            logger.LogInfo("Finished creating Minimap");

            return true;
        }

        private void Reset()
        {
            logger.LogDebug($"Clearing {nameof(trackedObjects)}");
            trackedObjects.Clear();

            logger.LogDebug($"Clearing {nameof(trackedDimensions)}");
            trackedDimensions.Clear();

            logger.LogDebug($"Destroying {nameof(minimap)}");
            minimap.Destroy();

            dynamicScanTimer.Reset();
            cooldownTimer.Reset();

            // mark the scene as scannable again so we scan for chests etc..
            scannedStaticObjects = false;

            cameraRig = null;
        }

        private void ScanScene()
        {
            // don't scan if the minimap isn't enabled
            if (Enabled == false)
            {
                return;
            }
            // when other mods hook into the various global events
            // and this method throws exceptions, the entire event will throw and fail to invoke their methods
            // as a result, this method should never throw an exception and should output meaningful
            // errors
            try
            {
                if (cooldownTimer.Started is false)
                {
                    cooldownTimer.Start();
                }
                else if (cooldownTimer.Expired is false)
                {
                    return;
                }

                cooldownTimer.Reset();
                cooldownTimer.Start();

                logger.LogDebug("Scanning Scene");

                logger.LogDebug("Clearing dynamically tracked objects");
                ClearDynamicTrackedObjects();

                logger.LogDebug("Scanning static types");
                ScanStaticTypes();

                logger.LogDebug("Scanning dynamic types");
                ScanDynamicTypes();
            }
            catch (Exception e)
            {
                logger.LogException(e, $"Fatal exception within minimap");

                // intentionally consume the error, again we never want to throw an exception in
                // a global event delegate (unless we're the last event, but that is never garunteed)
            }
        }

        private ITrackedObjectScanner CreateChestScanner(Func<ChestBehavior, bool> activeChecker)
        {
            bool TryGetPurchaseToken<T>(T value, out string out_token) where T : MonoBehaviour
            {
                var token = value ? value.GetComponent<PurchaseInteraction>()?.contextToken : null;

                // mods that implement ChestBehaviour, may not also PurchaseInteraction
                if (token is null)
                {
                    logger.LogDebug($"No {nameof(PurchaseInteraction)} component on {typeof(T).Name}. GameObject.name = {value.gameObject.name}");
                }

                out_token = token;

                return token != null;
            }

            // given any chest object this will retur true if it should be displayed as a chest
            bool ChestSelector(ChestBehavior chest)
            {
                if (TryGetPurchaseToken(chest, out string token))
                {
                    return token.Contains("CHEST") && token != "LUNAR_CHEST_CONTEXT" && token.Contains("STEALTH") == false;
                }

                return false;
            }

            bool LunarPodSelector(ChestBehavior chest)
            {
                if (TryGetPurchaseToken(chest, out string token))
                {
                    return token == "LUNAR_CHEST_CONTEXT";
                }

                return false;
            }

            return new MultiKindScanner<ChestBehavior>(false, new MonoBehaviorScanner<ChestBehavior>(logger),
                new MonoBehaviourSorter<ChestBehavior>(
                [
                    new DefaultSorter<ChestBehavior>(InteractableKind.Chest, x => x.gameObject, ChestSelector, activeChecker),
                    new DefaultSorter<ChestBehavior>(InteractableKind.LunarPod,  x => x.gameObject, LunarPodSelector, activeChecker),
                ]), trackedDimensions, spriteManager, () => GetPlayerPosition().y);
        }

        private ITrackedObjectScanner CreateGenericInteractionScanner()
        {
            bool PortalSelector(GenericInteraction interaction) => interaction && (interaction.contextToken?.Contains("PORTAL") ?? false);

            bool GenericActiveChecker(GenericInteraction interaction) => !interaction || interaction.isActiveAndEnabled;

            return new MultiKindScanner<GenericInteraction>(false, new MonoBehaviorScanner<GenericInteraction>(logger),
                new MonoBehaviourSorter<GenericInteraction>(
                [
                    new DefaultSorter<GenericInteraction>(InteractableKind.Portal, DefaultConverter, PortalSelector, GenericActiveChecker),
                    new DefaultSorter<GenericInteraction>(InteractableKind.Special, DefaultConverter, DefaultSelector, GenericActiveChecker)
                ]), trackedDimensions, spriteManager, () => GetPlayerPosition().y);
        }

        private ITrackedObjectScanner CreatePurchaseInteractionScanner()
        {
            bool FanSelector(PurchaseInteraction interaction) => interaction && interaction.contextToken == "FAN_CONTEXT";

            bool PrinterSelector(PurchaseInteraction interaction) => interaction && (interaction.contextToken?.Contains("DUPLICATOR") ?? false);

            bool ShopSelector(PurchaseInteraction interaction) => interaction && (interaction.contextToken?.Contains("TERMINAL") ?? false);

            bool EquipmentSelector(PurchaseInteraction interaction) => interaction && (interaction.contextToken?.Contains("EQUIPMENTBARREL") ?? false);

            bool GoldShoresSelector(PurchaseInteraction interaction) => interaction && (interaction.contextToken?.Contains("GOLDSHORE") ?? false);

            bool GoldShoresBeaconSelector(PurchaseInteraction interaction) => interaction && (interaction.contextToken?.Contains("TOTEM") ?? false);

            bool InteractionActiveChecker(PurchaseInteraction interaction) => !interaction || interaction.available;

            return new MultiKindScanner<PurchaseInteraction>(false, new MonoBehaviorScanner<PurchaseInteraction>(logger),
                new MonoBehaviourSorter<PurchaseInteraction>(
                [
                    new DefaultSorter<PurchaseInteraction>(InteractableKind.Printer, DefaultConverter, PrinterSelector, InteractionActiveChecker),
                    new DefaultSorter<PurchaseInteraction>(InteractableKind.Special, DefaultConverter, FanSelector, InteractionActiveChecker),
                    new DefaultSorter<PurchaseInteraction>(InteractableKind.Shop, DefaultConverter, ShopSelector, InteractionActiveChecker),
                    new DefaultSorter<PurchaseInteraction>(InteractableKind.Equipment, DefaultConverter, EquipmentSelector, InteractionActiveChecker),
                    new DefaultSorter<PurchaseInteraction>(InteractableKind.Portal, DefaultConverter, GoldShoresSelector, InteractionActiveChecker),
                    new DefaultSorter<PurchaseInteraction>(InteractableKind.Totem, DefaultConverter, GoldShoresBeaconSelector, GoldShoresSelector),
                ]), trackedDimensions, spriteManager, () => GetPlayerPosition().y);
        }

        private void CreateStaticScanners()
        {
            bool DefaultActiveChecker<T>(T value) where T: MonoBehaviour
            {
                // default always active;
                if (!value)
                    return true;

                if (value is PurchaseInteraction isInteraction)
                {
                    return isInteraction.available;
                }

                if (value.TryGetComponent<PurchaseInteraction>(out var interaction)) 
                {
                    return interaction.available;
                }

                // default always active;
                return true;
            }

            ITrackedObjectScanner SimpleScanner<T>(InteractableKind kind, Func<T, bool> activeChecker = null, Func<T, bool> selector = null, Func<T, GameObject> converter = null) where T: MonoBehaviour
            {
                return new SingleKindScanner<T>(
                    kind: kind,
                    dynamic: false,
                    scanner: new MonoBehaviorScanner<T>(logger),
                    range: trackedDimensions,
                    spriteManager: spriteManager,
                    playerHeightRetriever: () => GetPlayerPosition().y,
                    converter: converter ?? DefaultConverter,
                    activeChecker: activeChecker ?? DefaultActiveChecker,
                    selector: selector
                );
            }

            staticScanners = new ITrackedObjectScanner[] {
                CreateChestScanner(DefaultActiveChecker),
                CreatePurchaseInteractionScanner(),
                CreateGenericInteractionScanner(),
                SimpleScanner<RouletteChestController>(InteractableKind.Chest),
                SimpleScanner<ShrineBloodBehavior>(InteractableKind.Shrine),
                SimpleScanner<ShrineChanceBehavior>(InteractableKind.Shrine),
                SimpleScanner<ShrineCombatBehavior>(InteractableKind.Shrine),
                SimpleScanner<ShrineHealingBehavior>(InteractableKind.Shrine),
                SimpleScanner<ShrineRestackBehavior>(InteractableKind.Shrine),
                SimpleScanner<ShrineBossBehavior>(InteractableKind.Shrine),
                SimpleScanner<ScrapperController>(InteractableKind.Utility),
                SimpleScanner<TeleporterInteraction>(InteractableKind.Teleporter, activeChecker: (teleporter) => teleporter.activationState != TeleporterInteraction.ActivationState.Charged),
                SimpleScanner<SummonMasterBehavior>(InteractableKind.Drone),
                SimpleScanner<BarrelInteraction>(InteractableKind.Barrel, activeChecker: barrel => !barrel.Networkopened),
            };
        }

        private ITrackedObjectScanner CreateAliveEntityScanner()
        {
            bool EnemyMonsterSelector(TeamComponent team) => team && team.teamIndex == TeamIndex.Monster;

            bool EnemyLunarSelector(TeamComponent team) => team && team.teamIndex == TeamIndex.Lunar;

            bool EnemyVoidSelector(TeamComponent team) => team && team.teamIndex == TeamIndex.Void;

            bool NeutralSelector(TeamComponent team) => team && team.teamIndex == TeamIndex.Neutral;

            bool MinionSelector(TeamComponent team)
            {
                if (!team)
                    return false;

                var isAlly = team.teamIndex == TeamIndex.Player;
                if (!team.body)
                {
                    return isAlly;
                }

                return isAlly && !team.body.isPlayerControlled;
            }

            bool PlayerSelector(TeamComponent team)
            {
                if (!team)
                    return false;

                var isOwner = team?.GetComponent<NetworkStateMachine>()?.hasAuthority;
                var isPlayer = team?.GetComponent<CharacterBody>()?.isPlayerControlled;
                if (isOwner is null || isPlayer is null)
                {
                    return false;
                }
                return (isOwner == false) && (isPlayer == true);
            }

            return new MultiKindScanner<TeamComponent>(true, new MonoBehaviorScanner<TeamComponent>(logger), 
                new MonoBehaviourSorter<TeamComponent>(
                [
                    new DefaultSorter<TeamComponent>(InteractableKind.EnemyMonster, DefaultConverter, EnemyMonsterSelector, DefaultSelector),
                    new DefaultSorter<TeamComponent>(InteractableKind.EnemyLunar, DefaultConverter, EnemyLunarSelector, DefaultSelector),
                    new DefaultSorter<TeamComponent>(InteractableKind.EnemyVoid, DefaultConverter, EnemyVoidSelector, DefaultSelector),
                    new DefaultSorter<TeamComponent>(InteractableKind.Minion, DefaultConverter, MinionSelector, DefaultSelector),
                    new DefaultSorter<TeamComponent>(InteractableKind.Player, DefaultConverter, PlayerSelector, DefaultSelector),
                    new DefaultSorter<TeamComponent>(InteractableKind.Neutral, DefaultConverter, NeutralSelector, x => !x || x.gameObject.activeSelf),
                ]), trackedDimensions, spriteManager, () => GetPlayerPosition().y);
        }

        private Vector3 GetPlayerPosition()
        {
            if (cameraRig == null)
            {
                cameraRig = Camera.main.transform.parent.GetComponent<CameraRigController>();

                if (cameraRig == null)
                {
                    logger.LogError("Failed to retrieve camera rig in scene to retrieve camera position");
                }
            }

            return cameraRig.target?.transform?.position ?? Camera.main.transform.position;
        }

        private void CreateDynamicScanners()
        {
            dynamicScanners = new ITrackedObjectScanner[]
            {
                CreateAliveEntityScanner(),
                new SingleKindScanner<GenericPickupController>(
                    kind: InteractableKind.Item,
                    dynamic: true,
                    scanner: new MonoBehaviorScanner<GenericPickupController>(logger),
                    range: trackedDimensions,
                    spriteManager: spriteManager,
                    playerHeightRetriever: () => GetPlayerPosition().y,
                    converter: x => x.gameObject,
                    activeChecker: x => true
                ),
                CreateGenericInteractionScanner()
            };
        }

        private void ScanStaticTypes()
        {
            // if we have alreadys scanned don't scan again until we die or the scene changes (this method has sever performance implications)
            if (scannedStaticObjects)
            {
                return;
            }

            for (int i = 0; i < staticScanners.Length; i++)
            {
                staticScanners[i].ScanScene(trackedObjects);
            }

            scannedStaticObjects = true;
        }

        private void ScanDynamicTypes()
        {
            for (int i = 0; i < dynamicScanners.Length; i++)
            {
                dynamicScanners[i].ScanScene(trackedObjects);
            }
        }

        private void ClearDynamicTrackedObjects()
        {
            if (scannedStaticObjects is false)
            {
                return;
            }

            for (int i = 0; i < trackedObjects.Count; i++)
            {
                var obj = trackedObjects[i];

                if (obj.DynamicObject)
                {
                    obj.Destroy();
                    trackedObjects.RemoveAt(i);
                }
            }
        }
    }
}
