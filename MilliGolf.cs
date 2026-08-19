using Modding;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MilliGolf.Rando.Interop;
using MilliGolf.Rando.Manager;
using MilliGolf.Rando.Settings;
using Satchel;
using UObject = UnityEngine.Object;
using uuiText = UnityEngine.UI.Text;

namespace MilliGolf {
    public class MilliGolf: Mod, ILocalSettings<LocalGolfSettings>, IGlobalSettings<GolfRandoSettings> {
        public static bool doCustomLoad = false;
        public static bool isInGolfRoom = false;
        public static bool isInUnofficialCourse = false;
        public static bool wasInCustomRoom = false;
        public static bool isStubbornLocked = false;
        public static int ballCam = 0;
        public static string tinkDamager;
        public static int currentScore;
        public static int totalScore = 0;
        public static string currentHoleTarget = "bot1";
        public static string dreamReturnDoor = "door1";
        public static Dictionary<string, Dictionary<string, GameObject>> prefabs;
        public static Dictionary<string, string> stubbornLockAreas;
        public static CameraLockArea currentStubbornLockArea;
        public static GameObject millibelleRef;
        public static GameObject holeRef;
        public static PlayMakerFSM areaTitleRef;

        private static MilliGolf _instance;

        public MilliGolf() : base() {
            _instance = this;
        }

        internal static MilliGolf Instance {
            get {
                if(_instance == null) {
                    throw new InvalidOperationException($"{nameof(MilliGolf)} was never initialized");
                }
                return _instance;
            }
        }

        new public string GetName() => "MilliGolf";
        public override string GetVersion() => "1.3.1.1";

        public static LocalGolfSettings golfData { get; set; } = new();
        public void OnLoadLocal(LocalGolfSettings g) => golfData = g;
        public LocalGolfSettings OnSaveLocal() => golfData;
        public static GolfRandoSettings GS { get; set; } = new();
        public void OnLoadGlobal(GolfRandoSettings s) => GS = s;
        public GolfRandoSettings OnSaveGlobal() => GS;

        public delegate void BoardCheck(int score);
        public static event BoardCheck OnBoardCheck;
        public delegate string AttunedPreview();
        public static event AttunedPreview OnAttunedPreview;
        public delegate string AscendedPreview();
        public static event AscendedPreview OnAscendedPreview;
        public delegate string RadiantPreview();
        public static event RadiantPreview OnRadiantPreview;
        public delegate string MasterPreview();
        public static event MasterPreview OnMasterPreview;
        public delegate string GrandmasterPreview();
        public static event GrandmasterPreview OnGrandmasterPreview;

        public override void Initialize(Dictionary<string, Dictionary<string, GameObject>> preloadedObjects) {
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged += earlySceneChange;
            On.GameManager.OnNextLevelReady += lateSceneChange;
            On.PlayMakerFSM.OnEnable += editFSM;
            On.BossSummaryBoard.Show += locationCheck;
            ModHooks.TakeHealthHook += takeHealth;
            ModHooks.NewGameHook += onNewGameSetup;
            ModHooks.SavegameLoadHook += onSaveLoadSetup;
            ModHooks.BeforeSceneLoadHook += onBeforeSceneLoad;

            CameraMods.Initialize();

            prefabs = preloadedObjects;
            GameObject millibellePrefab = preloadedObjects["Ruins_Bathhouse"]["Banker Spa NPC"];
            UObject.DontDestroyOnLoad(millibellePrefab);
            millibellePrefab.FindGameObjectInChildren("NPC").SetActive(false);
            millibellePrefab.FindGameObjectInChildren("Dream Dialogue").SetActive(false);
            millibellePrefab.FindGameObjectInChildren("Content Audio").SetActive(false);
            millibellePrefab.AddComponent(typeof(collisionDetector));

            Assembly assembly = Assembly.GetExecutingAssembly();
            string defaultCourses = assembly.GetManifestResourceNames().Single(str => str.EndsWith("DefaultCourses.json"));
            using Stream defaultCourseStream = assembly.GetManifestResourceStream(defaultCourses);
            foreach(GolfJsonScene gScene in new ParseJson(defaultCourseStream).parseCourses()) {
                gScene.translate().create(false);
            }
            string jsonFileName = Path.GetDirectoryName(assembly.Location) + "/CustomCourses.json";
            if(!File.Exists(jsonFileName)) {
                string customCourses = assembly.GetManifestResourceNames().Single(str => str.EndsWith("CustomCourses.json"));
                using Stream customCourseStream = assembly.GetManifestResourceStream(customCourses);
                using FileStream createdFile = File.Create(jsonFileName);
                customCourseStream.CopyTo(createdFile);
                customCourseStream.Dispose();//possibly unnecessary
            }
            foreach(GolfJsonScene gcScene in new ParseJson(jsonFileName).parseCourses()) {
                gcScene.translate().create(true);
            }

            // Only make a Rando Integration if Randomizer 4 is active.
            if(ModHooks.GetMod("Randomizer 4") is Mod) {
                GolfManager.Hook();

                if(ModHooks.GetMod("RandoSettingsManager") is Mod) {
                    RSM_Interop.Hook();
                }
            }

            ModHooks.GetPlayerBoolHook += BoolOverride;
            On.HeroController.CanDash += DashOverride;
            On.HeroController.CanWallJump += ClawOverride;
            On.HeroController.CanWallSlide += ClawOverride2;
            On.HeroController.LookForInput += ClawOverride3; // Really? Does it have to get THIS hacky?
            On.HeroController.CanSuperDash += CDashOverride;
            On.HeroController.CanDreamNail += DreamNailOverride;
            On.HeroController.CanDoubleJump += WingsOverride;
            On.DeactivateInDarknessWithoutLantern.Start += HazardRespawn;
            // This needs to run after ItemChanger's hook when IC is installed,
            // therefore we need to hook before IC does.
            // While IC loads earlier due to load priority, it actually only
            // installs its hook on this method upon loading or creating a save,
            // so hooking here does allow us to beat IC to the punch.
            On.GameManager.BeginSceneTransition += TransitionToGolfRoom;
            if(ModHooks.GetMod("ItemChangerMod") is Mod && ModHooks.GetMod("Randomizer 4") is Mod) {
                GolfManager.AddICHooks();
            }
        }

        private void HazardRespawn(On.DeactivateInDarknessWithoutLantern.orig_Start orig, DeactivateInDarknessWithoutLantern self) {
            orig(self);

            if(self.GetComponent<HazardRespawnTrigger>() != null && isInGolfRoom) {
                self.gameObject.SetActive(true);
            }
        }

        internal const string golfTransitionSuffix = "_golf";
        internal const string golfTentTransition = "room_milligolf";

        private void TransitionToGolfRoom(On.GameManager.orig_BeginSceneTransition orig, GameManager self, GameManager.SceneLoadInfo info) {
            if(info.EntryGateName != null && info.EntryGateName.EndsWith(golfTransitionSuffix)) {
                info.EntryGateName = info.EntryGateName.Substring(0, info.EntryGateName.Length - golfTransitionSuffix.Length);
                doCustomLoad = true;
            }
            orig(self, info);
        }

        private bool DreamNailOverride(On.HeroController.orig_CanDreamNail orig, HeroController self) {
            bool boolLessDNail = (
                !GameManager.instance.isPaused &&
                self.hero_state != ActorStates.no_input &&
                !self.cState.dashing &&
                !self.cState.backDashing &&
                (!self.cState.attacking || !(ReflectionHelper.GetField<HeroController, float>(self, "attack_time") <
                                             ReflectionHelper.GetField<HeroController, float>(self, "ATTACK_RECOVERY_TIME"))) &&
                !self.controlReqlinquished &&
                !self.cState.hazardDeath &&
                !self.cState.hazardRespawning &&
                self.GetComponent<Rigidbody2D>().velocity.y > -0.1f &&
                !self.cState.recoilFrozen &&
                !self.cState.recoiling &&
                !self.cState.transitioning &&
                self.cState.onGround
            );

            return orig(self) || (boolLessDNail && isInGolfRoom);
        }

        private bool BoolOverride(string name, bool orig) {
            List<string> trueOverriden = new() {
                "hasMap",
                "hasNailArt"
            };
            List<string> falseOverriden = new() {
                "crossroadsInfected",
                "hasLantern"
            };
            if(falseOverriden.Contains(name)) {
                return orig && !isInGolfRoom;
            }
            if(trueOverriden.Contains(name)) {
                return orig || isInGolfRoom;
            }
            return orig;
        }

        private bool WingsOverride(On.HeroController.orig_CanDoubleJump orig, HeroController self) {
            bool boolessWings = (
                !self.controlReqlinquished &&
                !ReflectionHelper.GetField<HeroController, bool>(self, "doubleJumped") &&
                !ReflectionHelper.GetField<HeroController, bool>(self, "inAcid") &&
                self.hero_state != ActorStates.no_input &&
                self.hero_state != ActorStates.hard_landing &&
                self.hero_state != ActorStates.dash_landing &&
                !self.cState.dashing && !self.cState.wallSliding &&
                !self.cState.backDashing &&
                !self.cState.attacking &&
                !self.cState.bouncing &&
                !self.cState.shroomBouncing &&
                !self.cState.onGround
            );

            return orig(self) || (boolessWings && isInGolfRoom);
        }

        private bool CDashOverride(On.HeroController.orig_CanSuperDash orig, HeroController self) {
            bool boolessCDash = (
                !GameManager._instance.isPaused &&
                self.hero_state != ActorStates.no_input &&
                !self.cState.dashing &&
                !self.cState.hazardDeath &&
                !self.cState.hazardRespawning &&
                !self.cState.backDashing &&
                (!self.cState.attacking || !(ReflectionHelper.GetField<HeroController, float>(self, "attack_time") <
                                             ReflectionHelper.GetField<HeroController, float>(self, "ATTACK_RECOVERY_TIME"))) &&
                !self.cState.slidingLeft &&
                !self.cState.slidingRight &&
                !self.controlReqlinquished &&
                !self.cState.recoilFrozen &&
                !self.cState.recoiling &&
                !self.cState.transitioning &&
                (self.cState.onGround || self.cState.wallSliding)
            );
            return orig(self) || (boolessCDash && isInGolfRoom);
        }

        private bool ClawOverride(On.HeroController.orig_CanWallJump orig, HeroController self) {
            if(isInGolfRoom) {
                if(self.cState.touchingNonSlider) {
                    return false;
                }

                if(self.cState.wallSliding) {
                    return true;
                }

                if(self.cState.touchingWall && !self.cState.onGround) {
                    return true;
                }

                return false;
            }
            return orig(self);
        }

        private bool ClawOverride2(On.HeroController.orig_CanWallSlide orig, HeroController self) {
            bool canSlide = (
                (self.cState.wallSliding && GameManager._instance.isPaused) ||
                !self.cState.touchingNonSlider &&
                !ReflectionHelper.GetField<HeroController, bool>(self, "inAcid") &&
                !self.cState.dashing &&
                !self.cState.onGround &&
                !self.cState.recoiling &&
                !GameManager._instance.isPaused &&
                !self.controlReqlinquished &&
                !self.cState.transitioning &&
                (self.cState.falling || self.cState.wallSliding) &&
                !self.cState.doubleJumping &&
                self.CanInput()
            );

            return orig(self) || (canSlide && isInGolfRoom);
        }

        private void ClawOverride3(On.HeroController.orig_LookForInput orig, HeroController self) {
            orig(self);

            bool canSlide = (
                (self.cState.wallSliding && GameManager._instance.isPaused) ||
                !self.cState.touchingNonSlider &&
                !ReflectionHelper.GetField<HeroController, bool>(self, "inAcid") &&
                !self.cState.dashing &&
                !self.cState.onGround &&
                !self.cState.recoiling &&
                (PlayerData.instance.GetBool("hasWalljump") || isInGolfRoom) &&
                !GameManager._instance.isPaused &&
                !self.controlReqlinquished &&
                !self.cState.transitioning &&
                (self.cState.falling || self.cState.wallSliding) &&
                !self.cState.doubleJumping &&
                self.CanInput()
            );

            if(canSlide && !self.cState.attacking) {
                if(self.touchingWallL && InputHandler.Instance.inputActions.left.IsPressed && !self.cState.wallSliding) {
                    ReflectionHelper.SetFieldSafe(self, "airDashed", false);
                    ReflectionHelper.SetFieldSafe(self, "doubleJumped", false);
                    self.wallSlideVibrationPlayer.Play();
                    self.cState.wallSliding = true;
                    self.cState.willHardLand = false;
                    self.wallSlidingL = true;
                    self.wallSlidingR = false;
                    self.FaceLeft();
                }

                if(self.touchingWallR && InputHandler.Instance.inputActions.right.IsPressed && !self.cState.wallSliding) {
                    ReflectionHelper.SetFieldSafe(self, "airDashed", false);
                    ReflectionHelper.SetFieldSafe(self, "doubleJumped", false);
                    self.wallSlideVibrationPlayer.Play();
                    self.cState.wallSliding = true;
                    self.cState.willHardLand = false;
                    self.wallSlidingL = false;
                    self.wallSlidingR = true;
                    self.FaceRight();
                }
            }
        }

        private bool DashOverride(On.HeroController.orig_CanDash orig, HeroController self) {
            bool boolessDash = (
                self.hero_state != ActorStates.no_input &&
                self.hero_state != ActorStates.hard_landing &&
                self.hero_state != ActorStates.dash_landing &&
                ReflectionHelper.GetField<HeroController, float>(self, "dashCooldownTimer") <= 0 &&
                !self.cState.dashing &&
                !self.cState.backDashing &&
                (!self.cState.attacking || !(ReflectionHelper.GetField<HeroController, float>(self, "attack_time") <
                                             ReflectionHelper.GetField<HeroController, float>(self, "ATTACK_RECOVERY_TIME"))) &&
                !self.cState.preventDash &&
                (self.cState.onGround || !ReflectionHelper.GetField<HeroController, bool>(self, "airDashed") || self.cState.wallSliding) &&
                !self.cState.hazardDeath
            );
            return orig(self) || (boolessDash && isInGolfRoom);
        }

        public override List<(string, string)> GetPreloadNames() {
            return new List<(string, string)> {
                ("Ruins_Bathhouse","Banker Spa NPC"),
                ("GG_Atrium","GG_big_door_part_small"),
                ("GG_Atrium","Col_Glow_Remasker (1)"),
                ("GG_Atrium","Col_Glow_Remasker (2)"),
                ("GG_Atrium","Door_Workshop"),
                ("Town", "divine_tent"),
                ("Town", "room_divine"),
                ("Town", "grimm_tents/main_tent/Grimm_town_signs_0001_1"),
                ("Fungus2_14", "Quirrel Mantis NPC"),
                ("Fungus2_06", "GameObject"),
                ("GG_Workshop", "GG_Statue_Knight")
            };
        }

        private void onNewGameSetup() {
            bool isRando = false;
            if(ModHooks.GetMod("Randomizer 4") is Mod) {
                isRando = GolfManager.IsRandoSave();
            }
            GolfManager.SaveSettings.randoSettings.Enabled = GolfManager.GlobalSettings.Enabled && isRando;
            GolfManager.SaveSettings.randoSettings.CourseAccess = GolfManager.GlobalSettings.CourseAccess;
            GolfManager.SaveSettings.randoSettings.CourseCompletion = GolfManager.GlobalSettings.CourseCompletion;
            GolfManager.SaveSettings.randoSettings.GlobalGoals = GolfManager.GlobalSettings.GlobalGoals;
            GolfManager.SaveSettings.randoSettings.CourseTransitions = GolfManager.GlobalSettings.CourseTransitions;
            startGameSetup();
        }

        private void onSaveLoadSetup(int obj) {
            startGameSetup();
        }

        private void startGameSetup() {
            addDialogue();
            stubbornLockAreas = new();
            stubbornLockAreas.Add("Hive_03", "CameraLockArea (13)");
            stubbornLockAreas.Add("Deepnest_East_11", "CameraLockArea (1)");
        }

        private void editFSM(On.PlayMakerFSM.orig_OnEnable orig, PlayMakerFSM self) {
            orig(self);
            if(self.gameObject.name.StartsWith("Banker Spa NPC(Clone)") && self.gameObject.scene.name != "Ruins_Bathhouse") {
                if(self.FsmName == "Hit Around") {
                    self.GetValidState("Init").RemoveAction(2);
                    self.GetValidState("Withdrawn").RemoveAction(0);

                    FsmState leftState = self.GetValidState("Hit Left");
                    FsmOwnerDefault leftFlungObject = ((FlingObject)leftState.GetAction(1)).flungObject;
                    leftState.RemoveAction(1);
                    leftState.InsertAction(new customFlingObject(self.gameObject, leftFlungObject, 22, 120), 1);

                    FsmState rightState = self.GetValidState("Hit Right");
                    FsmOwnerDefault rightFlungObject = ((FlingObject)rightState.GetAction(1)).flungObject;
                    rightState.RemoveAction(1);
                    rightState.InsertAction(new customFlingObject(self.gameObject, rightFlungObject, 22, 60), 1);

                    FsmState upState = self.GetValidState("Hit Up");
                    FsmOwnerDefault upFlungObject = ((FlingObject)upState.GetAction(1)).flungObject;
                    upState.RemoveAction(1);
                    upState.InsertAction(new customFlingObject(self.gameObject, upFlungObject, 30, 90), 1);
                }
                else if(self.FsmName == "tink_effect") {
                    self.GetValidState("Get Damager Parameters").InsertAction(new storeTinkDamager(self), 1);
                }
            }
            else if(self.gameObject.name.StartsWith("Quirrel Mantis NPC(Clone)") && self.FsmName == "FSM") {
                isGolfingBool golfQuirrel = new();
                golfQuirrel.isTrue = FsmEvent.GetFsmEvent("ENABLE");
                self.AddState("Keep");
                self.AddFirstAction("Check", golfQuirrel);
                self.AddTransition("Check", "ENABLE", "Keep");
            }
            else if(self.gameObject.name == "Surface Water Region" && self.FsmName == "Acid Armour Check") {
                isGolfingBool golfIsma = new();
                golfIsma.isTrue = FsmEvent.GetFsmEvent("ENABLE");
                self.AddFirstAction("Check", golfIsma);
            }
            else if(self.FsmName == "Door Control") {
                try {
                    self.GetValidState("In Range").InsertAction(new renameEnterLabel(self, "ENTER"), 1);
                }
                catch(Exception) { }
            }
            else if(self.gameObject.name == "RestBench (1)" && self.FsmName == "Bench Control" && self.gameObject.scene.name == "GG_Workshop" && (doCustomLoad || isInGolfRoom)) {
                self.FsmVariables.GetFsmBool("Set Respawn").Value = false;
            }
            else if(self.gameObject.name == "Knight" && self.FsmName == "Map Control") {
                FsmState singleTapState = self.AddState("Single Tap");
                self.GetValidState("Check Double").ChangeTransition("FINISHED", "Single Tap");
                singleTapState.AddTransition("FINISHED", "Reset Timer");
                singleTapState.AddAction(new toggleCamTarget());
            }
            else if(self.gameObject.name == "Knight" && self.FsmName == "Dream Nail") {
                // Allow DreamGate when golfing
                isGolfingBool golfDG = new();
                golfDG.isTrue = FsmEvent.GetFsmEvent("GOLFING");
                self.AddState("Golf DG");
                self.AddAction("Golf DG", self.GetAction("Dream Gate?", 1));
                self.AddAction("Golf DG", self.GetAction("Dream Gate?", 2));
                self.AddAction("Golf DG", self.GetAction("Dream Gate?", 3));
                self.AddFirstAction("Dream Gate?", golfDG);
                self.AddTransition("Dream Gate?", "GOLFING", "Golf DG");
                self.AddTransition("Golf DG", "SET", "Set Charge Start");
                self.AddTransition("Golf DG", "WARP", "Warp Charge Start");
                self.AddTransition("Golf DG", "FINISHED", "Slash Antic");
                //deny setting a dgate
                isGolfingBool isGolfingSet = new();
                isGolfingSet.isTrue = FsmEvent.GetFsmEvent("FAIL");
                self.GetValidState("Can Set?").InsertAction(isGolfingSet, 3);
                //remove essence requirement to warp
                FsmState canWarpState = self.GetValidState("Can Warp?");
                isGolfingIntCompare isGolfingNoEssence = new((IntCompare)canWarpState.Actions[2]);
                canWarpState.RemoveAction(2);
                canWarpState.InsertAction(isGolfingNoEssence, 2);
                //deny warping out of Hall
                isDreamgatingForbidden noGating = new();
                noGating.isTrue = FsmEvent.GetFsmEvent("FAIL");
                canWarpState.InsertAction(noGating, 9);
                //cancel custom load if warp fails
                self.GetValidState("Warp Fail").AddCustomAction(() => 
                {
                    // This is the only thing we need to reset; other things
                    // that make you dream warp will overwrite the dream return
                    // scene and door.
                    doCustomLoad = false;
                });
                //set destination
                canWarpState.InsertAction(new setDreamReturnScene(), 7);
                isGolfingBool isGoBoo = new();
                isGoBoo.isTrue = FsmEvent.GetFsmEvent("DREAM");
                self.GetValidState("Leave Type").AddAction(isGoBoo);
                self.GetValidState("Leave Dream").InsertAction(new setDreamReturnDoor(self), 7);
                canWarpState.InsertAction(new whitePalaceGolfOverride(), 9);
                //shorten warp
                isGolfingWait customWait = new(2, 0.25f);
                customWait.finishEvent = FsmEvent.GetFsmEvent("CHARGED");
                FsmState warpChargeState = self.GetValidState("Warp Charge");
                warpChargeState.RemoveAction(0);
                warpChargeState.InsertAction(customWait, 0);
                //single-tap quick-reset
                FsmState resetState = self.AddState("Golf Quick Reset");
                self.ChangeTransition("Charge", "CANCEL", "Golf Quick Reset");
                self.ChangeTransition("Entry Cancel Check", "CANCEL", "Golf Quick Reset");
                resetState.AddTransition("FINISHED", "Charge Cancel");
                resetState.AddAction(new golfQuickReset());
            }
            else if(self.gameObject.name == "Knight" && self.FsmName == "Nail Arts") {
                isGolfingBool isGolfingSet = new();
                isGolfingSet.isTrue = FsmEvent.GetFsmEvent("GOLFING");

                self.AddFirstAction("Has Dash?", isGolfingSet);
                self.AddTransition("Has Dash?", "GOLFING", "Dash Slash Ready");

                self.AddFirstAction("Has Cyclone?", isGolfingSet);
                self.AddTransition("Has Cyclone?", "GOLFING", "Flash");

                self.AddFirstAction("Has G Slash?", isGolfingSet);
                self.AddTransition("Has G Slash?", "GOLFING", "Flash 2");
            }
            else if(self.gameObject.name == "Area Title" && self.FsmName == "Area Title Control") {
                areaTitleRef = self;
                self.GetValidState("Visited Check").InsertAction(new pbVisitedCheck(), 0);
                self.GetValidState("Set Text Large").InsertAction(new pbSetTitleText(), 3);
                self.GetValidState("Set Text Small").InsertAction(new pbSetTitleText(), 0);
            }
        }

        private string onBeforeSceneLoad(string arg) {
            if(!doCustomLoad && GameManager.instance.IsGameplayScene()) {
                HeroController.instance.gameObject.FindGameObjectInChildren("Vignette").SetActive(true);
            }
            if(!doCustomLoad && wasInCustomRoom) {
                wasInCustomRoom = false;
            }
            return arg;
        }

        private void earlySceneChange(Scene from, Scene to) {
            bool accessRandomized = golfData.randoSettings.Enabled && golfData.randoSettings.CourseAccess;
            if(to.name == "Town" && !doCustomLoad) {
                {
                    if(!accessRandomized || accessRandomized && golfData.randoSaveState.courseAccess.AnyTrue()) {
                        GameObject golfTransition = GameObject.Instantiate(prefabs["Town"]["room_divine"], new Vector3(195.2094f, 7.8265f, 0), Quaternion.identity);
                        golfTransition.RemoveComponent<DeactivateIfPlayerdataFalse>();
                        golfTransition.SetActive(true);
                        PlayMakerFSM doorControlFSM = PlayMakerFSM.FindFsmOnGameObject(golfTransition, "Door Control");
                        FsmState changeSceneState = doorControlFSM.GetValidState("Change Scene");
                        BeginSceneTransition changeSceneAction = ((BeginSceneTransition)changeSceneState.GetAction(1));
                        changeSceneAction.sceneName = "GG_Workshop";
                        changeSceneAction.entryGateName = "left1" + golfTransitionSuffix;
                        // The default name of the door includes (Clone)(Clone),
                        // which is not valid in rando logic. We could alias it
                        // just for rando, but why complicate things?
                        TransitionPoint door = golfTransition.GetComponent<TransitionPoint>();
                        door.name = golfTentTransition;
                        // These are not needed for the transition to work normally,
                        // but in transition rando ItemChanger looks at TransitionPoints to determine
                        // which gate we're going through so it can know what to redirect us to,
                        // so this info needs to be correct.
                        door.targetScene = "GG_Workshop";
                        door.entryPoint = "left1" + golfTransitionSuffix;
                        golfTransition.GetComponent<TransitionPoint>().name = golfTentTransition;
                        GameObject golfTent = GameObject.Instantiate(prefabs["Town"]["divine_tent"], new Vector3(205.1346f, 13.1462f, 47.2968f), Quaternion.identity);
                        setupTentPrefab(golfTent);
                        golfTent.GetComponent<PlayMakerFSM>().enabled = false;
                        golfTent.SetActive(true);
                    }
                }
            }
            else if(to.name == "GG_Workshop" && doCustomLoad) {
                for(int i = 0; i < GolfScene.courseList.Count; i++) {
                    placeDoor(i * 10 + 27, 7.68f, GolfScene.courseDict[GolfScene.courseList[i]], false);
                }
                for(int j = 0; j < GolfScene.customCourseList.Count; j++) {
                    placeDoor(j * 10 + 27, 37.68f, GolfScene.customCourseDict[GolfScene.customCourseList[j]], true);
                }
            }
        }

        private void lateSceneChange(On.GameManager.orig_OnNextLevelReady orig, GameManager self) {
            isInGolfRoom = false;
            orig(self);
            isInUnofficialCourse = false;
            ballCam = 0;
            isStubbornLocked = false;
            if(doCustomLoad) {
                addDialogue();
                wasInCustomRoom = true;
                bool officialContainsName = GolfScene.courseList.Contains(self.sceneName);
                bool unofficialContainsName = GolfScene.customCourseList.Contains(self.sceneName) && !officialContainsName;
                if(officialContainsName || unofficialContainsName) {
                    isInGolfRoom = true;
                    isInUnofficialCourse = unofficialContainsName;
                    Dictionary<string, GolfScene> tempDict = isInUnofficialCourse ? GolfScene.customCourseDict : GolfScene.courseDict;
                    List<string> tempList = isInUnofficialCourse ? GolfScene.customCourseList : GolfScene.courseList;
                    currentScore = 0;
                    GameCameras.instance.geoCounter.geoTextMesh.text = currentScore.ToString();
                    HeroController.instance.gameObject.FindGameObjectInChildren("Vignette").SetActive(false);
                    currentHoleTarget = tempDict[self.sceneName].holeTarget;
                    if(tempDict[self.sceneName].millibelleSpawn != null) {
                        millibelleRef = GameObject.Instantiate(prefabs["Ruins_Bathhouse"]["Banker Spa NPC"], tempDict[self.sceneName].millibelleSpawn, Quaternion.identity);
                        millibelleRef.SetActive(true);
                        millibelleRef.GetComponent<MeshRenderer>().sortingOrder = 1;
                    }
                    if(!isInUnofficialCourse && tempDict[self.sceneName].hasQuirrel) {
                        (float, float, bool, string) qd = tempDict[self.sceneName].quirrelData;
                        GameObject quirrel = addQuirrel(qd.Item1, qd.Item2, qd.Item3, qd.Item4);
                        if(self.sceneName == "Town") {
                            PlayMakerFSM.FindFsmOnGameObject(quirrel, "npc_control").FsmVariables.GetFsmFloat("Move To Offset").SafeAssign(1);
                        }
                    }
                    (string, float, float) fd = tempDict[self.sceneName].flagData;
                    addFlag(fd.Item1, fd.Item2, fd.Item3);
                    if(tempDict[self.sceneName].customHoleObject) {
                        GameObject customHole = GameObject.Instantiate(prefabs["Fungus2_06"]["GameObject"], tempDict[self.sceneName].customHolePosition.Item1, Quaternion.identity);
                        customHole.transform.localScale = tempDict[self.sceneName].customHolePosition.Item2;
                        customHole.layer = LayerMask.NameToLayer("Terrain");
                        customHole.name = "Custom Hole";
                        customHole.SetActive(true);
                    }
                    TransitionPoint[] transitions = GameObject.FindObjectsOfType<TransitionPoint>();
                    foreach(TransitionPoint tp in transitions) {
                        if(tp.gameObject.name != tempDict[self.sceneName].startTransition) {
                            disableTransition(tp.gameObject);
                        }
                        else {
                            tp.name += golfTransitionSuffix;
                            tp.targetScene = "GG_Workshop";
                            string entry = "door" + (tempList.IndexOf(self.sceneName) + (isInUnofficialCourse ? 19 : 1)) + golfTransitionSuffix;
                            tp.entryPoint = entry;
                            PlayMakerFSM tpfsm = tp.gameObject.LocateMyFSM("Door Control");
                            if(tpfsm != null) {
                                tpfsm.FsmVariables.GetFsmString("New Scene").Value = "GG_Workshop";
                                tpfsm.FsmVariables.GetFsmString("Entry Gate").Value = entry;
                            }
                        }
                    }
                    GameObject[] allGameObjects = GameObject.FindObjectsOfType<GameObject>();
                    foreach(GameObject go in allGameObjects) {
                        if(go.name == currentHoleTarget) {
                            holeRef = go;
                        }
                        if(stubbornLockAreas.ContainsKey(self.sceneName) && go.name == stubbornLockAreas[self.sceneName]) {
                            currentStubbornLockArea = go.GetComponent<CameraLockArea>();
                        }
                        if(go.layer == LayerMask.NameToLayer("Enemies")) {
                            if(self.sceneName != "White_Palace_19") {
                                go.SetActive(false);
                            }
                        }

                        // Remove several interactable items that could affect rando runs
                        if(go.name == "RestBench") {
                            go.SetActive(false);
                        }
                        if(go.name.Contains("Grub")) {
                            go.SetActive(false);
                        }
                        if(go.name == "Cornifer") {
                            go.SetActive(false);
                        }
                        if(go.name == "Dream Plant") {
                            go.SetActive(false);
                        }
                        if(go.name.Contains("Soul Totem")) {
                            go.SetActive(false);
                        }
                        if(go.name.Contains("Shiny Item")) {
                            go.SetActive(false);
                        }
                        if(go.name.Contains("Geo Rock")) {
                            go.SetActive(false);
                        }
                        if(go.name.Contains("Breakable Wall")) {
                            go.SetActive(false);
                        }
                        if(go.name.Contains("One Way Wall")) {
                            go.SetActive(false);
                        }
                        if(go.name.Contains("Quake Floor")) {
                            go.SetActive(false);
                        }
                        if(go.name.Contains("Dream Dialogue")) {
                            go.SetActive(false);
                        }

                        if(tempDict[self.sceneName].objectsToDisable.Contains(go.name)) {
                            go.SetActive(false);
                        }
                        if(tempDict[self.sceneName].childrenToDisable.ContainsKey(go.name)) {
                            List<string> parents = new(tempDict[self.sceneName].childrenToDisable.Keys);
                            foreach(string parent in parents) {
                                List<string> children = tempDict[self.sceneName].childrenToDisable[parent];
                                foreach(string child in children) {
                                    go.FindGameObjectInChildren(child).SetActive(false);
                                }
                            }
                        }
                    }
                }
                else if(self.sceneName == "GG_Workshop") {
                    isInGolfRoom = true;
                    isInUnofficialCourse = false;
                    calculateTotalScore();
                    BossStatue[] statues = GameObject.FindObjectsOfType<BossStatue>();
                    foreach(BossStatue bs in statues) {
                        bs.gameObject.SetActive(false);
                    }
                    TransitionPoint[] transitions = GameObject.FindObjectsOfType<TransitionPoint>();
                    foreach(TransitionPoint tp in transitions) {
                        if(tp.name == "left1") {
                            tp.targetScene = "Town";
                            tp.entryPoint = golfTentTransition;
                            tp.OnBeforeTransition += setCustomLoad.setCustomLoadFalse;
                        }
                        // ensure that these transitions are
                        // treated as distinct for randomization
                        // purposes when the player actually
                        // goes through them, while still allowing
                        // them to have the original name while
                        // loading
                        tp.name += golfTransitionSuffix;
                    }
                    GameObject[] gos = GameObject.FindObjectsOfType<GameObject>();
                    foreach(GameObject go in gos) {
                        if(go.name.StartsWith("BG_pillar") || go.name.Contains("clouds") || go.name == "gg_plat_float_wide") {
                            go.SetActive(false);
                        }
                        else if(go.name == "GG_Summary_Board") {
                            updateHallScoreboard(go);
                        }
                        else if(go.name == "Zote_Break_wall") {
                            go.GetComponent<PlayMakerFSM>().enabled = false;
                        }
                    }

                    bool radiantRando = golfData.randoSettings.Enabled && golfData.randoSettings.GlobalGoals >= MaxTier.Radiant;
                    if((!radiantRando && totalScore <= golfMilestones.Expert && golfData.scoreboard.Count == 18) || (radiantRando && golfData.randoSaveState.globalGoals >= 3)) {
                        addTrophyStatue(totalScore);
                    }
                    else {
                        addQuirrel(19.7f, 6.81f, true, "HALL");
                    }
                }
            }
            doCustomLoad = false;
        }

        public static void setupTentPrefab(GameObject tent) {
            List<string> toHide = new() {
                "haze2 (3)",
                "Grimm_tent_ext_0009_4 (3)",
                "haze2 (4)",
                "haze2 (5)",
                "Grimm_tent_ext_0008_5 (1)",
                "grimm torch (2)"
            };
            List<string> toGreenify = new() {
                "Grimm_tent_ext_0002_11",
                "Grimm_tent_ext_0002_11 (1)",
                "Grimm_tent_ext_2",
                "Grimm_tent_ext_3"
            };
            List<string> toBlacken = new() {
                "Grimm_tent_ext_0009_4 (3)",
                "Grimm_tent_ext_0009_4 (4)",
                "Grimm_tent_ext_0009_4 (5)"
            };
            foreach(string gameObject in toHide) {
                tent.FindGameObjectInChildren(gameObject).SetActive(false);
            }
            foreach(string gameObject in toGreenify) {
                tent.FindGameObjectInChildren(gameObject).GetComponent<SpriteRenderer>().color = new(0, 1, 0, 1);
            }
            foreach(string gameObject in toBlacken) {
                tent.FindGameObjectInChildren(gameObject).GetComponent<SpriteRenderer>().color = new(0, 0, 0, 1);
            }
        }

        public static void placeDoor(float x, float y, GolfScene room, bool isCustom) {
            string transitionName = "door" + (isCustom ? (GolfScene.customCourseList.IndexOf(room.scene) + 19) : (GolfScene.courseList.IndexOf(room.scene) + 1));
            // If the player doesn't have the access, still create a dummy transition point with
            // no collision so that they can enter from the other side
            // (only relevant for transition rando)
            bool accessRandomized = golfData.randoSettings.Enabled && golfData.randoSettings.CourseAccess;
            if(accessRandomized && !isCustom && !golfData.randoSaveState.courseAccess.GetVariable<bool>(room.scene)) {
                GameObject dummy = new();
                dummy.name = $"dummy transition from {room.scene}";
                dummy.transform.position = new Vector3(x, y, 8.13f);
                TransitionPoint dummyTP = dummy.AddComponent<TransitionPoint>();
                dummyTP.name = transitionName;
                dummyTP.nonHazardGate = true;
                dummy.SetActive(true);
                return;
            }

            GameObject.Instantiate(prefabs["GG_Atrium"]["GG_big_door_part_small"], new Vector3(x, y, 8.13f), Quaternion.identity).SetActive(true);

            GameObject glow1 = GameObject.Instantiate(prefabs["GG_Atrium"]["Col_Glow_Remasker (1)"], new Vector3(x - 0.6f, y - 4.3f, 11.99f), Quaternion.identity);
            glow1.SetActive(true);
            glow1.GetComponent<SpriteRenderer>().color = room.doorColor;

            GameObject glow2 = GameObject.Instantiate(prefabs["GG_Atrium"]["Col_Glow_Remasker (2)"], new Vector3(x + 1.63f, y - 5.72f, 18.99f), Quaternion.identity);
            glow2.SetActive(true);
            glow2.GetComponent<SpriteRenderer>().color = (room.secondaryDoorColor != Color.black ? room.secondaryDoorColor : room.doorColor);

            GameObject transition = GameObject.Instantiate(prefabs["GG_Atrium"]["Door_Workshop"], new Vector3(x - 0.2f, y - 1.92f, 0.2f), Quaternion.identity);
            TransitionPoint tp = transition.GetComponent<TransitionPoint>();
            transition.name = tp.name = transitionName;
            // Like in the main tent door, set the target room and gate so ItemChanger
            // can find them when redirecting this transition.
            tp.targetScene = room.scene;
            tp.entryPoint = room.startTransition + golfTransitionSuffix;
            transition.SetActive(true);
            PlayMakerFSM doorControlFSM = PlayMakerFSM.FindFsmOnGameObject(transition, "Door Control");
            FsmState changeSceneState = doorControlFSM.GetValidState("Change Scene");
            BeginSceneTransition enterAction = (BeginSceneTransition)(changeSceneState.GetAction(0));
            enterAction.sceneName = room.scene;
            enterAction.entryGateName = room.startTransition + golfTransitionSuffix;
            // If course transitions are enabled, dreamgating from courses
            // is disabled, so we don't need to set a gate.
            if (!(golfData.randoSettings.Enabled && golfData.randoSettings.CourseTransitions)) {
                changeSceneState.InsertAction(new setGate(transitionName), 1);
            }
            FsmState inRangeState = doorControlFSM.GetValidState("In Range");
            ((renameEnterLabel)inRangeState.GetAction(1)).newName = room.name;
        }

        private GameObject addQuirrel(float x, float y, bool faceRight, string dialogueKey) {
            GameObject quirrel = GameObject.Instantiate(prefabs["Fungus2_14"]["Quirrel Mantis NPC"], new Vector3(x, y, 0.006f), Quaternion.identity);
            if(faceRight) {
                quirrel.transform.SetScaleX(quirrel.transform.localScale.x * -1);
            }
            for(int i = 0; i < 4; i++) {
                quirrel.RemoveComponent<DeactivateIfPlayerdataTrue>();
            }
            quirrel.RemoveComponent<DeactivateIfPlayerdataFalse>();
            quirrel.FindGameObjectInChildren("Dream Dialogue").SetActive(false);

            PlayMakerFSM[] FSMs = quirrel.GetComponents<PlayMakerFSM>();
            foreach(PlayMakerFSM self in FSMs) {
                if(self.FsmName == "Conversation Control") {
                    FsmState choiceState = self.GetValidState("Choice");
                    FsmState golfState = self.CopyState("Repeat", "Golf");

                    choiceState.AddTransition("FINISHED", "Golf");

                    choiceState.RemoveAction(2);
                    choiceState.RemoveAction(0);

                    CallMethodProper callAction = golfState.GetAction(1) as CallMethodProper;
                    callAction.parameters[0].SetValue(dialogueKey);
                    callAction.parameters[1].SetValue("GolfQuirrel");
                }
            }
            quirrel.SetActive(true);
            return quirrel;
        }

        private void addFlag(string filename, float x, float y) {
            GameObject flagSign = GameObject.Instantiate(prefabs["Town"]["grimm_tents/main_tent/Grimm_town_signs_0001_1"], new Vector3(x, y, 0.023f), Quaternion.identity);
            SpriteRenderer sr = flagSign.GetComponent<SpriteRenderer>();
            Texture2D testFlagTexture = new(1, 1);
            using(Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"MilliGolf.Images.{filename}.png")) {
                byte[] bytes = new byte[stream.Length];
                stream.Read(bytes, 0, bytes.Length);
                testFlagTexture.LoadImage(bytes, false);
                testFlagTexture.name = "Golf Flag";
            }
            var testFlag = Sprite.Create(testFlagTexture, new Rect(0, 0, testFlagTexture.width, testFlagTexture.height), new Vector2(0.5f, 0.5f), 64, 0, SpriteMeshType.FullRect);
            sr.sprite = testFlag;
            flagSign.SetActive(true);
        }

        private void addTrophyStatue(int score) {
            GameObject statueBase = GameObject.Instantiate(prefabs["GG_Workshop"]["GG_Statue_Knight"], new Vector3(18.2f, 6.4f, 0.9277f), Quaternion.identity);
            statueBase.FindGameObjectInChildren("Statue").RemoveComponent<BossStatueCompletionStates>();
            GameObject knight1 = statueBase.FindGameObjectInChildren("Knight_v01");
            GameObject knight2 = statueBase.FindGameObjectInChildren("Knight_v02");
            GameObject knight3 = statueBase.FindGameObjectInChildren("Knight_v03");
            GameObject knightWithFsm;
            string outputText;

            List<GameObject> spotlightChildren = new();
            statueBase.FindGameObjectInChildren("Glow Response statue_beam").FindAllChildren(spotlightChildren);
            foreach(GameObject spotlightChild in spotlightChildren) {
                if(spotlightChild.name == "ground_haze") {
                    spotlightChild.SetActive(false);
                }
            }

            bool grandmasterRando = golfData.randoSettings.Enabled && golfData.randoSettings.GlobalGoals >= MaxTier.Grandmaster;
            bool masterRando = golfData.randoSettings.Enabled && golfData.randoSettings.GlobalGoals >= MaxTier.Master;
            if((!grandmasterRando && score <= golfMilestones.Grandmaster) || (grandmasterRando && golfData.randoSaveState.globalGoals >= 5)) {
                knight1.SetActive(false);
                knight2.SetActive(false);
                knight3.SetActive(true);
                knightWithFsm = knight3;
                List<GameObject> children = new();
                knight3.FindAllChildren(children);
                foreach(GameObject child in children) {
                    if(child.name == "wispy smoke (2)" || child.name == "wispy smoke (3)") {
                        child.SetActive(false);
                    }
                }
                outputText = "GRANDMASTER\r\nCongratulations, you have achieved an an extraordinary score and can officially consider yourself a God Gamer.<page>(No affiliation with fireb0rn and his academy)";
                if(golfData.randoSettings.GlobalGoals == MaxTier.Grandmaster && score > golfMilestones.Master) {
                    outputText += $"<page>...Is what you will be told if you ever get to do all courses in {golfMilestones.Grandmaster} hits or less, that is.";
                }
            }
            else if((!masterRando && score <= golfMilestones.Master) || (masterRando && golfData.randoSaveState.globalGoals >= 4)) {
                knight1.SetActive(false);
                knight2.SetActive(true);
                knight3.SetActive(false);
                knightWithFsm = knight2;
                List<GameObject> children = new();
                knight2.FindAllChildren(children);
                foreach(GameObject child in children) {
                    if(child.name == "Glow Response shade") {
                        child.SetActive(false);
                    }
                }
                outputText = "MASTER\r\nWell done achieving such an impressive score! You've come so far, but there's one more level to reach!";
                if(golfData.randoSettings.GlobalGoals >= MaxTier.Master && score > golfMilestones.Master) {
                    outputText += $"<page>...Is what you will be told if you ever get to do all courses in {golfMilestones.Master} hits or less, that is.";
                }
                if(!golfData.randoSettings.Enabled || (golfData.randoSettings.GlobalGoals < MaxTier.Grandmaster && golfData.scoreboard.Count == 18)) {
                    outputText += $"<page>{score - golfMilestones.Grandmaster} away from Grandmaster";
                }
            }
            else {
                knight1.SetActive(true);
                knight2.SetActive(false);
                knight3.SetActive(false);
                knightWithFsm = knight1;
                outputText = "EXPERT\r\nNice job reaching Radiant!";
                if(golfData.randoSettings.GlobalGoals >= MaxTier.Master && score > golfMilestones.Expert) {
                    outputText += $"<page>...Is what you will be told if you ever get to do all courses in {golfMilestones.Expert} hits or less, that is.";
                }
                else if(!golfData.randoSettings.Enabled || (golfData.randoSettings.GlobalGoals < MaxTier.Master && golfData.scoreboard.Count == 18)) {
                    outputText += $"<page>{score - golfMilestones.Master} away from Master";
                }
            }
            outputText += OnAttunedPreview?.Invoke();
            outputText += OnAscendedPreview?.Invoke();
            outputText += OnRadiantPreview?.Invoke();
            outputText += OnMasterPreview?.Invoke();
            outputText += OnGrandmasterPreview?.Invoke();
            statueBase.SetActive(true);
            PlayMakerFSM convo = PlayMakerFSM.FindFsmOnGameObject(knightWithFsm.FindGameObjectInChildren("Interact"), "Conversation Control");
            CallMethodProper callAction = (CallMethodProper)convo.GetValidState("Greet").Actions[1];
            callAction.parameters[1].SetValue("GolfTrophy");
            callAction.parameters[0].SetValue("TROPHY");
            convo.ChangeTransition("Greet", "CONVO_FINISH", "Talk Finish");

            FieldInfo field = typeof(Language.Language).GetField("currentEntrySheets", BindingFlags.NonPublic | BindingFlags.Static);
            Dictionary<string, Dictionary<string, string>> currentEntrySheets = (Dictionary<string, Dictionary<string, string>>)field.GetValue(null);
            currentEntrySheets["GolfTrophy"]["TROPHY"] = outputText;
        }

        public static int calculateTotalScore() {
            int total = 0;
            foreach(string key in golfData.scoreboard.Keys) {
                total += golfData.scoreboard[key];
            }
            totalScore = total;
            return totalScore;
        }

        private void locationCheck(On.BossSummaryBoard.orig_Show orig, BossSummaryBoard self) {
            int total = calculateTotalScore();
            bool completionRandomized = golfData.randoSettings.Enabled && golfData.randoSettings.CourseCompletion;
            bool globalGoals = golfData.randoSettings.Enabled && golfData.randoSettings.GlobalGoals > MaxTier.None;
            if(globalGoals) {
                // Set up hook for Rando to grant location checks - only if all courses were cleared
                if(golfData.scoreboard.Count == 18 && (!completionRandomized || !golfData.randoSaveState.courseCompletion.AnyFalse())) {
                    OnBoardCheck?.Invoke(total);
                }
            }
            orig(self);
        }

        private void updateHallScoreboard(GameObject summaryBoard) {
            BossSummaryBoard bsb = summaryBoard.GetComponent<BossSummaryBoard>();
            FieldInfo uiField = typeof(BossSummaryBoard).GetField("ui", BindingFlags.NonPublic | BindingFlags.Instance);
            BossSummaryUI bsu = uiField.GetValue(bsb) as BossSummaryUI;
            bsu.gameObject.FindGameObjectInChildren("Title_Text").SetActive(false);
            GameObject listGrid = bsu.gameObject.FindGameObjectInChildren("List Grid");
            List<GameObject> children = new();
            listGrid.FindAllChildren(children);
            List<GameObject> excessLines = new();
            bool accessRandomized = golfData.randoSettings.Enabled && golfData.randoSettings.CourseAccess;
            bool completionRandomized = golfData.randoSettings.Enabled && golfData.randoSettings.CourseCompletion;
            for(int i = 1; i < 10; i++) {
                string sceneName = GolfScene.courseDict[GolfScene.courseList[i - 1]].name;
                if(accessRandomized && !golfData.randoSaveState.courseAccess.GetVariable<bool>(GolfScene.courseDict[GolfScene.courseList[i - 1]].scene)) {
                    sceneName = "???";
                }
                children[i].FindGameObjectInChildren("Name_Text").GetComponent<uuiText>().text = sceneName;
            }
            for(int i = 10; i < 12; i++) {
                excessLines.Add(children[i]);
            }
            for(int i = 12; i < 21; i++) {
                string sceneName = GolfScene.courseDict[GolfScene.courseList[i - 12]].scene;
                string scoreText;

                if(!golfData.scoreboard.ContainsKey(sceneName)) {
                    scoreText = "---";
                }
                else if(completionRandomized && !golfData.randoSaveState.courseCompletion.GetVariable<bool>(GolfScene.courseDict[GolfScene.courseList[i - 12]].scene)) {
                    scoreText = "???";
                }
                else {
                    scoreText = golfData.scoreboard[sceneName].ToString();
                }
                children[i].FindGameObjectInChildren("Name_Text").GetComponent<uuiText>().text = scoreText;
            }
            uuiText totalText = children[21].FindGameObjectInChildren("Name_Text").GetComponent<uuiText>();
            totalText.text = "Total";
            totalText.alignment = TextAnchor.MiddleRight;
            excessLines.Add(children[22]);
            for(int i = 23; i < 32; i++) {
                string sceneName = GolfScene.courseDict[GolfScene.courseList[i - 14]].name;
                if(accessRandomized && !golfData.randoSaveState.courseAccess.GetVariable<bool>(GolfScene.courseDict[GolfScene.courseList[i - 14]].scene)) {
                    sceneName = "???";
                }
                children[i].FindGameObjectInChildren("Name_Text").GetComponent<uuiText>().text = sceneName;
            }
            excessLines.Add(children[33]);
            for(int i = 34; i < 43; i++) {
                string sceneName = GolfScene.courseDict[GolfScene.courseList[i - 25]].scene;
                string scoreText;
                if(!golfData.scoreboard.ContainsKey(sceneName)) {
                    scoreText = "---";
                }
                else if(completionRandomized && !golfData.randoSaveState.courseCompletion.GetVariable<bool>(GolfScene.courseDict[GolfScene.courseList[i - 25]].scene)) {
                    scoreText = "???";
                }
                else {
                    scoreText = golfData.scoreboard[sceneName].ToString();
                }
                children[i].FindGameObjectInChildren("Name_Text").GetComponent<uuiText>().text = scoreText;
            }
            if(!completionRandomized || (completionRandomized && !golfData.randoSaveState.courseCompletion.AnyFalse())) {
                children[32].FindGameObjectInChildren("Name_Text").GetComponent<uuiText>().text = totalScore.ToString();
            }
            else {
                children[32].FindGameObjectInChildren("Name_Text").GetComponent<uuiText>().text = "???";
            }
            for(int i = 43; i < Math.Min(45, children.Count); i++) {
                excessLines.Add(children[i]);
            }
            foreach(GameObject line in excessLines) {
                try {
                    line.FindGameObjectInChildren("Name_Text").GetComponent<uuiText>().text = "";
                }
                catch(NullReferenceException) { }
            }
            for(int i = 1; i < 45; i++) {
                GameObject image = children[i].FindGameObjectInChildren("Image");
                bool goalsRandomized = golfData.randoSettings.Enabled && golfData.randoSettings.GlobalGoals > MaxTier.None;
                if(i == 32) {
                    UnityEngine.UI.Image uuiImage = image.GetComponent<UnityEngine.UI.Image>();
                    if(!goalsRandomized) {
                        if(golfData.scoreboard.Count < 18 && (completionRandomized && golfData.randoSaveState.courseCompletion.AnyFalse() || !completionRandomized)) {
                            uuiImage.sprite = bsu.stateSprites[1];
                        }
                        else if(totalScore <= golfMilestones.Radiant) {
                            uuiImage.sprite = bsu.stateSprites[4];
                        }
                        else if(totalScore <= golfMilestones.Ascended) {
                            uuiImage.sprite = bsu.stateSprites[3];
                        }
                        else {
                            uuiImage.sprite = bsu.stateSprites[2];
                        }
                    }
                    else {
                        MaxTier randoGoals = golfData.randoSettings.GlobalGoals;
                        if(randoGoals >= MaxTier.Radiant) {
                            uuiImage.sprite = bsu.stateSprites[Math.Min(golfData.randoSaveState.globalGoals + 1, 4)];
                        }
                        else if(randoGoals == MaxTier.Ascended) {
                            int goals = golfData.randoSaveState.globalGoals;
                            if(golfData.scoreboard.Count == 18 && totalScore <= golfMilestones.Radiant) {
                                goals += 1;
                            }
                            uuiImage.sprite = bsu.stateSprites[Math.Min(goals + 1, 4)];
                        }
                        else {
                            int goals = golfData.randoSaveState.globalGoals;
                            if(golfData.scoreboard.Count == 18 && totalScore <= golfMilestones.Radiant) {
                                goals += 1;
                            }
                            if(golfData.scoreboard.Count == 18 && totalScore <= golfMilestones.Ascended) {
                                goals += 1;
                            }
                            uuiImage.sprite = bsu.stateSprites[Math.Min(goals + 1, 4)];
                        }
                    }
                    uuiImage.SetNativeSize();
                }
                else {
                    image.SetActive(false);
                }
            }
        }

        private void addDialogue() {
            FieldInfo field = typeof(Language.Language).GetField("currentEntrySheets", BindingFlags.NonPublic | BindingFlags.Static);
            Dictionary<string, Dictionary<string, string>> currentEntrySheets = (Dictionary<string, Dictionary<string, string>>)field.GetValue(null);
            {
                if(currentEntrySheets.ContainsKey("GolfQuirrel")) {
                    currentEntrySheets.Remove("GolfQuirrel");
                }
                Dictionary<string, string> golfQuirrel = new();

                // Quirrel having a slightly different dialogue depending on rando settings felt like a nice touch
                if(!golfData.randoSettings.Enabled) {
                    golfQuirrel.Add("HALL", "Welcome to MilliGolf, an 18-hole course and grand tour of Hallownest!<page>You may notice that you have full movement here, but don't worry. All outside progression will be restored when you leave.<page>Your total strokes will be tallied for each course on this scoreboard and will update if you beat your best score.<page>Maybe if you get a really good score, you'll get some kind of prize!<page>Best of luck and happy golfing!");
                }
                else {
                    string hallString = "Welcome to MilliGolf, an 18-hole course and grand tour of Hallownest!";
                    if(golfData.randoSettings.CourseAccess && golfData.randoSaveState.courseAccess.AnyFalse()) {
                        hallString += "<page>Not all 18 courses are open at the moment though, maybe that can change if you progress on your randomizer seed.";
                    }
                    hallString += "<page>You may notice that you have full movement here, but don't worry. All outside progression will be restored when you leave.";
                    if(golfData.randoSettings.CourseCompletion) {
                        hallString += "<page>Your total strokes will be tallied for each course on this scoreboard as soon as you find the proper checks.<page>Regardless of being able to see it or not, the strike count is kept track of and you'll get something for clearing courses too.";
                    }
                    else {
                        hallString += "<page>Your total strokes will be tallied for each course on this scoreboard and will update if you beat your best score.";
                    }
                    if(golfData.randoSettings.GlobalGoals > MaxTier.None) {
                        hallString += OnAttunedPreview?.Invoke();
                        hallString += OnAscendedPreview?.Invoke();
                        hallString += OnRadiantPreview?.Invoke();
                        hallString += OnMasterPreview?.Invoke();
                        hallString += OnGrandmasterPreview?.Invoke();
                    }
                    else {
                        hallString += "<page>Maybe if you get a really good score, you'll get some kind of prize!";
                    }
                    hallString += "<page>Best of luck and happy golfing!";
                    golfQuirrel.Add("HALL", hallString);
                }
                golfQuirrel.Add("DIRTMOUTH", "This is Millibelle the Banker/Thief. She will act as your golf ball and personal punching bag.<page>Try to punt her into the well with as few strokes as possible<page>When you're done (or at any time you wish), you may return to the Hall by coming back through this exit or using your dream gate.");
                golfQuirrel.Add("HIVE", "Some courses may prove to be quite tedious for normal nail slashes.<page>In some cases, you may find that a nail art is better suited for the situation.<page>Take a look at each art and observe how their effects differ.");
                golfQuirrel.Add("GREENPATH", "If you ever lose track of the ball or can't find the hole, single-tap your map button to switch between camera modes.<page>If you wish to start over in this room, you can single-tap your dream nail.");
                golfQuirrel.Add("EDGE", "Please watch your step!<page>This course has some nasty spikes, but you don't need to worry about your health when golfing.");
                currentEntrySheets.Add("GolfQuirrel", golfQuirrel);
            }
            if(!currentEntrySheets.ContainsKey("GolfTrophy")) {
                Dictionary<string, string> golfTrophy = new();
                golfTrophy.Add("TROPHY", "You should never see this text. Clearly something has gone wrong. Congrats on breaking the game I guess?");
                currentEntrySheets.Add("GolfTrophy", golfTrophy);
            }
        }

        public static void disableTransition(GameObject tp) {
            if(tp.TryGetComponent<BoxCollider2D>(out BoxCollider2D collider)) {
                collider.isTrigger = false;
            }
            if(tp.TryGetComponent<TransitionPoint>(out TransitionPoint actualTP)) {
                actualTP.enabled = false;
                if(!actualTP.isADoor) {
                    tp.layer = LayerMask.NameToLayer("Terrain");
                }
                else {
                    if(tp.TryGetComponent<BoxCollider2D>(out BoxCollider2D box)) {
                        box.enabled = false;
                    }
                }
            }
            
        }

        private int takeHealth(int damage) {
            return (isInGolfRoom ? 0 : damage);
        }

        public static void OnCollisionEnter2D(Collision2D collision) {
            if(collision.gameObject.name == currentHoleTarget) {
                GameObject explosionPrefab = GameManager.instance.gameObject.FindGameObjectInChildren("Gas Explosion Recycle L(Clone)");
                GameObject explosion = GameObject.Instantiate(explosionPrefab, millibelleRef.transform.position, Quaternion.identity);
                millibelleRef.SetActive(false);
                explosion.SetActive(true);
                completedHole(millibelleRef.scene.name, currentScore);
            }
        }

        public static void completedHole(string sceneName, int score) {
            if(!isInUnofficialCourse) {
                if(!golfData.scoreboard.ContainsKey(sceneName)) {
                    golfData.scoreboard.Add(sceneName, score);
                    pbTracker.update(score, true);
                }
                else if(golfData.scoreboard[sceneName] > score) {
                    golfData.scoreboard[sceneName] = score;
                    pbTracker.update(score, true);
                }
                else {
                    pbTracker.update(score, false);
                }
            }
            else {
                pbTracker.update(score, false);
            }
            if(areaTitleRef == null) {
                GameObject titleHolder = GameObject.Find("Area Title Holder");
                GameObject areaTitle = titleHolder.FindGameObjectInChildren("Area Title");
                areaTitleRef = areaTitle.GetComponent<PlayMakerFSM>();
            }
            areaTitleRef.gameObject.SetActive(false);
            areaTitleRef.gameObject.SetActive(true);
        }
    }

    public class pbTracker {
        public static bool isActivelyScoring;
        public static bool isPB;
        public static int score;
        public delegate void ScoreUpdated();
        public static event ScoreUpdated OnScoreUpdated;
        public static void update(int score, bool pb) {
            pbTracker.score = score;
            isPB = pb;
            isActivelyScoring = true;

            // Build hook for Completion item give
            if(ModHooks.GetMod("Randomizer 4") is Mod) {
                OnScoreUpdated?.Invoke();
            };
        }
    }
    public class renameEnterLabel: FsmStateAction {
        PlayMakerFSM self;
        public string newName;
        public renameEnterLabel(PlayMakerFSM self, string label) {
            this.self = self;
            newName = label;
        }
        public override void OnEnter() {
            try {
                TextMeshPro textMesh = self.FsmVariables.GetFsmGameObject("Prompt").Value.FindGameObjectInChildren("Enter").GetComponent<TextMeshPro>();
                textMesh.text = newName;
            }
            catch(NullReferenceException) { }
            Finish();
        }
    }

    public class storeTinkDamager: FsmStateAction {
        PlayMakerFSM self;
        public storeTinkDamager(PlayMakerFSM self) {
            this.self = self;
        }
        public override void OnEnter() {
            string damager;
            GameObject fsmGo = self.FsmVariables.GetFsmGameObject("Damager").Value;
            if(fsmGo != null && !string.IsNullOrEmpty(fsmGo.name)) {
                damager = fsmGo.name;
            }
            else {
                damager = "";
            }
            if(!string.IsNullOrEmpty(damager)) {
                MilliGolf.tinkDamager = damager;
                if(new List<string> { "Slash", "AltSlash", "UpSlash", "DownSlash", "Great Slash", "Dash Slash", "Hit L", "Hit R" }.Contains(damager)) {
                    MilliGolf.currentScore++;
                    GameCameras.instance.geoCounter.geoTextMesh.text = MilliGolf.currentScore.ToString();
                }
            }
            Finish();
        }
    }

    public class setCustomLoad: FsmStateAction {
        bool shouldCustom;
        public setCustomLoad(bool doCustom) {
            shouldCustom = doCustom;
        }
        public static void setCustomLoadTrue() {
            MilliGolf.doCustomLoad = true;
        }
        public static void setCustomLoadFalse() {
            MilliGolf.doCustomLoad = false;
        }
        public override void OnEnter() {
            MilliGolf.doCustomLoad = shouldCustom;
            Finish();
        }
    }

    public class setGate: FsmStateAction {
        string returnDoor;
        public setGate(string door) {
            returnDoor = door;
        }
        public override void OnEnter() {
            MilliGolf.dreamReturnDoor = returnDoor;
            Finish();
        }
    }

    public class customFlingObject: FlingObject {
        float speed, angle;
        GameObject gameObject;
        public customFlingObject(GameObject gameObject, FsmOwnerDefault owner, float speed, float angle) {
            this.gameObject = gameObject;
            flungObject = owner;
            this.speed = speed;
            this.angle = angle;
        }
        public override void OnEnter() {
            Vector3 position = gameObject.transform.position;
            gameObject.transform.position = new Vector3(position.x, position.y + 0.05f, position.z);
            speedMax = speedMin = speed * (PlayerData.instance.GetBool("equippedCharm_15") ? 1.2f : 1);
            switch(MilliGolf.tinkDamager) {
                case "Dash Slash":
                    switch(angle) {
                        case 120:
                            angleMax = angleMin = 160;
                            break;
                        case 60:
                            angleMax = angleMin = 20;
                            break;
                        default:
                            angleMax = angleMin = angle;
                            break;
                    }
                    break;
                case "Great Slash":
                    switch(angle) {
                        case 120:
                            angleMax = angleMin = 105;
                            break;
                        case 60:
                            angleMax = angleMin = 75;
                            break;
                        default:
                            angleMax = angleMin = angle;
                            break;
                    }
                    break;
                default:
                    angleMax = angleMin = angle;
                    break;
            }
            base.OnEnter();
        }
    }

    public class toggleCamTarget: FsmStateAction {
        public override void OnEnter() {
            if(MilliGolf.isInGolfRoom && GameManager.instance.sceneName != "GG_Workshop") {
                MilliGolf.ballCam = (MilliGolf.ballCam + 1) % 3;
                FieldInfo heroTransform = typeof(CameraTarget).GetField("heroTransform", BindingFlags.NonPublic | BindingFlags.Instance);
                switch(MilliGolf.ballCam) {
                    case 0:
                        if(MilliGolf.isStubbornLocked) {
                            GameCameras.instance.cameraController.ReleaseLock(MilliGolf.currentStubbornLockArea);
                            MilliGolf.isStubbornLocked = false;
                        }
                        while(CameraMods.lockZoneList.Count > 0) {
                            GameCameras.instance.cameraController.LockToArea(CameraMods.lockZoneList[0]);
                            CameraMods.lockZoneList.RemoveAt(0);
                        }
                        heroTransform.SetValue(GameCameras.instance.cameraTarget, HeroController.instance.transform);
                        break;
                    case 1:
                        CameraMods.lockZoneList.Clear();
                        while(GameCameras.instance.cameraController.lockZoneList.Count > 0) {
                            CameraLockArea currentZone = typeof(CameraController).GetField("currentLockArea", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(GameCameras.instance.cameraController) as CameraLockArea;
                            CameraMods.lockZoneList.Insert(0, currentZone);
                            GameCameras.instance.cameraController.ReleaseLock(currentZone);
                        }
                        heroTransform.SetValue(GameCameras.instance.cameraTarget, MilliGolf.millibelleRef.transform);
                        break;
                    case 2:
                        heroTransform.SetValue(GameCameras.instance.cameraTarget, MilliGolf.holeRef.transform);
                        string sceneName = GameManager.instance.sceneName;
                        if(MilliGolf.stubbornLockAreas.ContainsKey(sceneName)) {
                            GameCameras.instance.cameraController.LockToArea(MilliGolf.currentStubbornLockArea);
                            MilliGolf.isStubbornLocked = true;
                        }
                        break;
                }
            }
            Finish();
        }
    }

    public class isGolfingBool: FsmStateAction {
        public FsmEvent isTrue;
        public override void OnEnter() {
            if(MilliGolf.isInGolfRoom) {
                base.Fsm.Event(isTrue);
            }
            Finish();
        }
    }

    public class isDreamgatingForbidden: FsmStateAction {
        public FsmEvent isTrue;
        public override void OnEnter() {
            if(MilliGolf.isInGolfRoom && (GameManager.instance.sceneName == "GG_Workshop" || (MilliGolf.golfData.randoSettings.Enabled && MilliGolf.golfData.randoSettings.CourseTransitions))) {
                base.Fsm.Event(isTrue);
            }
            Finish();
        }
    }

    public class isGolfingIntCompare: IntCompare {
        public FsmEvent isGolfing;
        public isGolfingIntCompare(IntCompare source) {
            integer1 = source.integer1;
            integer2 = source.integer2;
            equal = source.equal;
            lessThan = source.lessThan;
            greaterThan = source.greaterThan;
            everyFrame = source.everyFrame;
        }
        public override void OnEnter() {
            if(MilliGolf.isInGolfRoom) {
                base.Fsm.Event(isGolfing);
                Finish();
            }
            else {
                base.OnEnter();
            }
        }
    }

    public class isGolfingWait: Wait {
        float normalTime;
        float golfTime;
        public isGolfingWait(float normalTime, float golfTime) {
            this.normalTime = normalTime;
            this.golfTime = golfTime;
        }
        public override void OnEnter() {
            if(MilliGolf.isInGolfRoom) {
                time = golfTime;
            }
            else {
                time = normalTime;
            }
            base.OnEnter();
        }
    }

    public class setDreamReturnScene: FsmStateAction {
        public override void OnEnter() {
            if(MilliGolf.isInGolfRoom) {
                PlayMakerFSM.FindFsmOnGameObject(HeroController.instance.gameObject, "Dream Nail").FsmVariables.GetFsmString("Gate Scene").Value = "GG_Workshop";
                PlayerData.instance.dreamReturnScene = "GG_Workshop";
                MilliGolf.doCustomLoad = true;
            }
            Finish();
        }
    }

    public class setDreamReturnDoor: FsmStateAction {
        PlayMakerFSM self;
        public setDreamReturnDoor(PlayMakerFSM self) {
            this.self = self;
        }
        public override void OnEnter() {
            self.FsmVariables.GetFsmString("Return Door").Value = (MilliGolf.isInGolfRoom ? MilliGolf.dreamReturnDoor : "door_dreamReturn");
            Finish();
        }
    }

    public class whitePalaceGolfOverride: FsmStateAction {
        public override void OnEnter() {
            if(MilliGolf.isInGolfRoom && GameManager.instance.sceneName == "White_Palace_19") {
                base.Fsm.Event(FsmEvent.GetFsmEvent("FINISHED"));
            }
            Finish();
        }
    }

    public class pbVisitedCheck: FsmStateAction {
        public override void OnEnter() {
            if(pbTracker.isActivelyScoring) {
                if(pbTracker.isPB) {
                    base.Fsm.Event(FsmEvent.GetFsmEvent("UNVISITED"));
                }
                else {
                    base.Fsm.Event(FsmEvent.GetFsmEvent("VISITED"));
                }
            }
            Finish();
        }
    }

    public class pbSetTitleText: FsmStateAction {
        public override void OnEnter() {
            if(pbTracker.isActivelyScoring) {
                FsmVariables areaTitleVars = MilliGolf.areaTitleRef.FsmVariables;
                areaTitleVars.GetFsmString("Title Sup").Value = (pbTracker.isPB ? "New Best" : "");
                areaTitleVars.GetFsmString("Title Main").Value = pbTracker.score.ToString();
                areaTitleVars.GetFsmString("Title Sub").Value = "";
                areaTitleVars.GetFsmBool("Title Has Subscript").Value = false;
                areaTitleVars.GetFsmBool("Title Has Superscript").Value = pbTracker.isPB;
            }
            pbTracker.isActivelyScoring = false;
            Finish();
        }
    }

    public class golfQuickReset: FsmStateAction {
        public override void OnEnter() {
            string scene = GameManager.instance.sceneName;
            if(MilliGolf.isInGolfRoom && scene != "GG_Workshop") {
                GolfScene gScene = MilliGolf.isInUnofficialCourse ? GolfScene.customCourseDict[scene] : GolfScene.courseDict[scene];
                HeroController.instance.gameObject.transform.position = gScene.knightSpawn;
                GameObject.Destroy(MilliGolf.millibelleRef.gameObject);
                MilliGolf.millibelleRef = GameObject.Instantiate(MilliGolf.prefabs["Ruins_Bathhouse"]["Banker Spa NPC"], gScene.millibelleSpawn, Quaternion.identity);
                MilliGolf.millibelleRef.SetActive(true);
                MilliGolf.millibelleRef.GetComponent<MeshRenderer>().sortingOrder = 1;
                MilliGolf.currentScore = 0;
                GameCameras.instance.geoCounter.geoTextMesh.text = MilliGolf.currentScore.ToString();
                if(MilliGolf.ballCam == 1) {
                    typeof(CameraTarget).GetField("heroTransform", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(GameCameras.instance.cameraTarget, MilliGolf.millibelleRef.transform);
                }
            }
            Finish();
        }
    }

    public class collisionDetector: MonoBehaviour {
        void OnCollisionEnter2D(Collision2D collision) {
            MilliGolf.OnCollisionEnter2D(collision);
        }
    }

    public class golfMilestones {
        public static int Ascended = 300;
        public static int Radiant = 250;
        public static int Expert = 250;
        public static int Master = 225;
        public static int Grandmaster = 200;
    }

    public class LocalGolfSettings {
        public Dictionary<string, int> scoreboard = new();
        public GolfRandoSettings randoSettings = new();
        public RandoSaveState randoSaveState = new();
    }
}