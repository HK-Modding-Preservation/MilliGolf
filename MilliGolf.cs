﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Modding;
using Satchel;
using UObject = UnityEngine.Object;
using uuiText = UnityEngine.UI.Text;

namespace MilliGolf {
    public class MilliGolf: Mod, ILocalSettings<LocalGolfSettings> {
        public static bool doCustomLoad = false;
        public static bool isInGolfRoom = false;
        public static bool wasInCustomRoom = false;
        public static bool ballCam = false;
        public static string tinkDamager;
        static Dictionary<string, Dictionary<string, GameObject>> prefabs;
        public static int currentScore;
        public static string currentHoleTarget = "bot1";
        public static string dreamReturnDoor = "door1";
        public static GameObject millibelleRef;
        public static PlayMakerFSM areaTitleRef;

        new public string GetName() => "MilliGolf";
        public override string GetVersion() => "1.0.1.0";

        public static LocalGolfSettings golfData { get; set; } = new();
        public void OnLoadLocal(LocalGolfSettings g) => golfData = g;
        public LocalGolfSettings OnSaveLocal() => golfData;

        public override void Initialize(Dictionary<string, Dictionary<string, GameObject>> preloadedObjects) {
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged += earlySceneChange;
            On.GameManager.OnNextLevelReady += lateSceneChange;
            On.PlayMakerFSM.OnEnable += editFSM;
            ModHooks.TakeHealthHook += takeHealth;
            ModHooks.NewGameHook += onNewGameSetup;
            ModHooks.SavegameLoadHook += onSaveLoadSetup;
            ModHooks.BeforeSceneLoadHook += onBeforeSceneLoad;
            On.UIManager.ReturnToMainMenu += onReturnToMainMenu;

            CameraMods.Initialize();

            prefabs = preloadedObjects;
            GameObject millibellePrefab = preloadedObjects["Ruins_Bathhouse"]["Banker Spa NPC"];
            UObject.DontDestroyOnLoad(millibellePrefab);
            millibellePrefab.FindGameObjectInChildren("NPC").SetActive(false);
            millibellePrefab.FindGameObjectInChildren("Dream Dialogue").SetActive(false);
            millibellePrefab.FindGameObjectInChildren("Content Audio").SetActive(false);
            millibellePrefab.AddComponent(typeof(collisionDetector));

            createCourseData();
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
                ("Fungus2_06", "GameObject")
            };
        }

        private void onNewGameSetup() {
            startGameSetup();
        }

        private void onSaveLoadSetup(int obj) {
            startGameSetup();
        }

        private IEnumerator onReturnToMainMenu(On.UIManager.orig_ReturnToMainMenu orig, UIManager self) {
            progressionLog.restoreProgression();
            yield return orig(self);
        }

        private void startGameSetup() {
            addDialogue();
        }

        private void editFSM(On.PlayMakerFSM.orig_OnEnable orig, PlayMakerFSM self) {
            orig(self);
            if(self.gameObject.name == "Banker Spa NPC(Clone)(Clone)" && self.gameObject.scene.name != "Ruins_Bathhouse") {
                if(self.FsmName == "Hit Around") {
                    self.GetState("Init").RemoveAction(2);
                    self.GetState("Withdrawn").RemoveAction(0);

                    FsmState leftState = self.GetState("Hit Left");
                    FsmOwnerDefault leftFlungObject = ((FlingObject)leftState.GetAction(1)).flungObject;
                    leftState.RemoveAction(1);
                    leftState.InsertAction(new customFlingObject(self.gameObject, leftFlungObject, 22, 120), 1);

                    FsmState rightState = self.GetState("Hit Right");
                    FsmOwnerDefault rightFlungObject = ((FlingObject)rightState.GetAction(1)).flungObject;
                    rightState.RemoveAction(1);
                    rightState.InsertAction(new customFlingObject(self.gameObject, rightFlungObject, 22, 60), 1);

                    FsmState upState = self.GetState("Hit Up");
                    FsmOwnerDefault upFlungObject = ((FlingObject)upState.GetAction(1)).flungObject;
                    upState.RemoveAction(1);
                    upState.InsertAction(new customFlingObject(self.gameObject, upFlungObject, 30, 90), 1);
                }
                else if(self.FsmName == "tink_effect") {
                    self.GetState("Get Damager Parameters").InsertAction(new storeTinkDamager(self), 1);
                }
            }
            else if(self.FsmName == "Door Control") {
                self.GetState("In Range").InsertAction(new renameEnterLabel(self, "ENTER"), 1);
            }
            else if(self.gameObject.name == "RestBench (1)" && self.FsmName == "Bench Control" && self.gameObject.scene.name == "GG_Workshop") {
                self.FsmVariables.GetFsmBool("Set Respawn").Value = false;
            }
            else if(self.gameObject.name == "Knight" && self.FsmName == "Map Control") {
                FsmState singleTapState = self.AddState("Single Tap");
                self.GetState("Check Double").ChangeTransition("FINISHED", "Single Tap");
                singleTapState.AddTransition("FINISHED", "Reset Timer");
                singleTapState.AddAction(new toggleCamTarget());
            }
            else if(self.gameObject.name == "Knight" && self.FsmName == "Dream Nail") {
                //deny setting a dgate
                isGolfingBool isGolfingSet = new();
                isGolfingSet.isTrue = FsmEvent.GetFsmEvent("FAIL");
                self.GetState("Can Set?").InsertAction(isGolfingSet, 3);
                //remove essence requirement to warp
                FsmState canWarpState = self.GetState("Can Warp?");
                isGolfingIntCompare isGolfingNoEssence = new((IntCompare)canWarpState.Actions[2]);
                canWarpState.RemoveAction(2);
                canWarpState.InsertAction(isGolfingNoEssence, 2);
                //deny warping out of Hall
                isInGolfHallBool inHall = new();
                inHall.isTrue = FsmEvent.GetFsmEvent("FAIL");
                canWarpState.InsertAction(inHall, 9);
                //set destination
                canWarpState.InsertAction(new setDreamReturnScene(), 7);
                isGolfingBool isGoBoo = new();
                isGoBoo.isTrue = FsmEvent.GetFsmEvent("DREAM");
                self.GetState("Leave Type").AddAction(isGoBoo);
                self.GetState("Leave Dream").InsertAction(new setDreamReturnDoor(self), 7);
                canWarpState.InsertAction(new whitePalaceGolfOverride(), 9);
            }
            else if(self.gameObject.name == "Area Title" && self.FsmName == "Area Title Control") {
                areaTitleRef = self;
                self.GetState("Visited Check").InsertAction(new pbVisitedCheck(), 0);
                self.GetState("Set Text Large").InsertAction(new pbSetTitleText(), 3);
                self.GetState("Set Text Small").InsertAction(new pbSetTitleText(), 0);
            }
        }

        private string onBeforeSceneLoad(string arg) {
            if(!doCustomLoad && GameManager.instance.IsGameplayScene()) {
                HeroController.instance.gameObject.FindGameObjectInChildren("Vignette").SetActive(true);
            }
            if(!doCustomLoad && wasInCustomRoom) {
                progressionLog.restoreProgression();
                wasInCustomRoom = false;
            }
            return arg;
        }

        private void earlySceneChange(Scene from, Scene to) {
            if(to.name == "Town" && !doCustomLoad) {
                GameObject golfTransition = GameObject.Instantiate(prefabs["Town"]["room_divine"], new Vector3(195.2094f, 7.8265f, 0), Quaternion.identity);
                golfTransition.RemoveComponent<DeactivateIfPlayerdataFalse>();
                golfTransition.SetActive(true);
                
                PlayMakerFSM doorControlFSM = PlayMakerFSM.FindFsmOnGameObject(golfTransition, "Door Control");
                FsmState changeSceneState = doorControlFSM.GetState("Change Scene");
                ((BeginSceneTransition)changeSceneState.GetAction(1)).sceneName = "GG_Workshop";
                changeSceneState.InsertAction(new setCustomLoad(true), 1);
                changeSceneState.InsertAction(new logProgression(), 2);

                GameObject golfTent = GameObject.Instantiate(prefabs["Town"]["divine_tent"], new Vector3(205.1346f, 13.1462f, 47.2968f), Quaternion.identity);
                setupTentPrefab(golfTent);
                golfTent.GetComponent<PlayMakerFSM>().enabled = false;
                golfTent.SetActive(true);
            }
            else if(to.name == "GG_Workshop" && doCustomLoad) {
                for(int i = 0; i < golfScene.courseList.Count; i++) {
                    placeDoor(i * 10 + 27, 7.68f, golfScene.courseDict[golfScene.courseList[i]]);
                }
            }
        }

        private void lateSceneChange(On.GameManager.orig_OnNextLevelReady orig, GameManager self) {
            orig(self);
            isInGolfRoom = false;
            ballCam = false;
            if(doCustomLoad) {
                wasInCustomRoom = true;
                if(golfScene.courseList.Contains(self.sceneName)) {
                    isInGolfRoom = true;
                    HeroController.instance.gameObject.FindGameObjectInChildren("Vignette").SetActive(false);
                    currentHoleTarget = golfScene.courseDict[self.sceneName].holeTarget;
                    if(golfScene.courseDict[self.sceneName].millibelleSpawn != null) {
                        millibelleRef = GameObject.Instantiate(prefabs["Ruins_Bathhouse"]["Banker Spa NPC"], golfScene.courseDict[self.sceneName].millibelleSpawn, Quaternion.identity);
                        millibelleRef.SetActive(true);
                        millibelleRef.GetComponent<MeshRenderer>().sortingOrder = 1;
                    }
                    if(golfScene.courseDict[self.sceneName].hasQuirrel) {
                        (float, float, bool, string) qd = golfScene.courseDict[self.sceneName].quirrelData;
                        GameObject quirrel = addQuirrel(qd.Item1, qd.Item2, qd.Item3, qd.Item4);
                        if(self.sceneName == "Town") {
                            PlayMakerFSM.FindFsmOnGameObject(quirrel,"npc_control").FsmVariables.GetFsmFloat("Move To Offset").SafeAssign(1);
                        }
                    }
                    (string, float, float) fd = golfScene.courseDict[self.sceneName].flagData;
                    addFlag(fd.Item1, fd.Item2, fd.Item3);
                    if(golfScene.courseDict[self.sceneName].customHoleObject) {
                        GameObject customHole = GameObject.Instantiate(prefabs["Fungus2_06"]["GameObject"], golfScene.courseDict[self.sceneName].customHolePosition.Item1, Quaternion.identity);
                        customHole.transform.localScale = golfScene.courseDict[self.sceneName].customHolePosition.Item2;
                        customHole.layer = LayerMask.NameToLayer("Terrain");
                        customHole.name = "Custom Hole";
                        customHole.SetActive(true);
                    }
                    TransitionPoint[] transitions = GameObject.FindObjectsOfType<TransitionPoint>();
                    foreach(TransitionPoint tp in transitions) {
                        if(tp.gameObject.name != golfScene.courseDict[self.sceneName].startTransition) {
                            disableTransition(tp.gameObject);
                        }
                        else {
                            tp.targetScene = "GG_Workshop";
                            tp.entryPoint = "door" + (golfScene.courseList.IndexOf(self.sceneName) + 1);
                            tp.OnBeforeTransition += setCustomLoad.setCustomLoadTrue;
                        }
                    }
                    GameObject[] allGameObjects = GameObject.FindObjectsOfType<GameObject>();
                    foreach(GameObject go in allGameObjects) {
                        if(go.layer == LayerMask.NameToLayer("Enemies")) {
                            if(self.sceneName != "White_Palace_19") {
                                go.SetActive(false);
                            }
                        }
                        if(golfScene.courseDict[self.sceneName].objectsToDisable.Contains(go.name)) {
                            go.SetActive(false);
                        }
                        if(golfScene.courseDict[self.sceneName].childrenToDisable.ContainsKey(go.name)) {
                            List<string> parents = new(golfScene.courseDict[self.sceneName].childrenToDisable.Keys);
                            foreach(string parent in parents) {
                                List<string> children = golfScene.courseDict[self.sceneName].childrenToDisable[parent];
                                foreach(string child in children) {
                                    go.FindGameObjectInChildren(child).SetActive(false);
                                }
                            }
                        }
                    }
                }
                else if(self.sceneName == "GG_Workshop") {
                    isInGolfRoom = true;
                    progressionLog.overrideProgression();
                    BossStatue[] statues = GameObject.FindObjectsOfType<BossStatue>();
                    foreach(BossStatue bs in statues) {
                        bs.gameObject.SetActive(false);
                    }
                    GameObject[] gos = GameObject.FindObjectsOfType<GameObject>();
                    foreach(GameObject go in gos) {
                        if(go.name.StartsWith("BG_pillar") || go.name.Contains("clouds")) {
                            go.SetActive(false);
                        }
                        else if(go.name == "GG_Summary_Board") {
                            updateHallScoreboard(go);
                        }
                    }

                    TransitionPoint workshopExit = GameObject.Find("left1").GetComponent<TransitionPoint>();
                    workshopExit.targetScene = "Town";
                    workshopExit.entryPoint = "room_divine(Clone)(Clone)";
                    workshopExit.OnBeforeTransition += setCustomLoad.setCustomLoadFalse;
                    workshopExit.OnBeforeTransition += progressionLog.restoreProgression;

                    addQuirrel(19.7f, 6.81f, true, "HALL");
                }
            }
            doCustomLoad = false;
            currentScore = 0;
        }

        public static void createCourseData() {
            golfScene dirtmouth = new("Dirtmouth", "Town", "left1", "bot1");
            dirtmouth.doorColor = new Color(0.156f, 0.2f, 0.345f, 0.466f);
            dirtmouth.millibelleSpawn = new Vector3(11, 44.8f, 0.006f);
            dirtmouth.objectsToDisable.Add("RestBench");
            dirtmouth.hasQuirrel = true;
            dirtmouth.quirrelData = (7.6f, 44.81f, true, "DIRTMOUTH");
            dirtmouth.flagData = ("flagSignSW", 188.4f, 8.81f);

            golfScene crossroads = new("Forgotten Crossroads", "Crossroads_07", "right1", "bot1");
            crossroads.doorColor = new Color(0.4054f, 0.4264f, 0.8f, 0.466f);
            crossroads.millibelleSpawn = new Vector3(36, 82.8f, 0.006f);
            crossroads.flagData = ("flagSignSW", 23.2f, 4.81f);

            golfScene grounds = new("Resting Grounds", "RestingGrounds_05", "left2", "bot1");
            grounds.doorColor = new Color(0.5309f, 0.5961f, 0.9054f, 0.466f);
            grounds.millibelleSpawn = new Vector3(17, 78.8f, 0.006f);
            grounds.objectsToDisable.Add("Quake Floor");
            grounds.objectsToDisable.Add("grave_tall_pole_sil (2)");
            grounds.flagData = ("flagSignE", 29.8f, 3.81f);
            
            golfScene hive = new("The Hive", "Hive_03", "right1", "bot1");
            hive.doorColor = new Color(1, 0.7516f, 0.3917f, 0.466f);
            hive.millibelleSpawn = new Vector3(129.5f, 143.8f, 0.006f);
            hive.hasQuirrel = true;
            hive.quirrelData = (120.4f, 143.81f, false, "HIVE");
            hive.flagData = ("flagSignSW", 80.4f, 112.81f);

            golfScene greenpath = new("Greenpath", "Fungus1_31", "right1", "bot1");
            greenpath.doorColor = new Color(0.3909f, 0.6868f, 0.3696f, 0.466f);
            greenpath.millibelleSpawn = new Vector3(31, 115.8f, 0.006f);
            greenpath.objectsToDisable.Add("RestBench");
            greenpath.objectsToDisable.Add("Toll Gate");
            greenpath.objectsToDisable.Add("Toll Gate (1)");
            greenpath.objectsToDisable.Add("Toll Gate Machine");
            greenpath.objectsToDisable.Add("Toll Gate Machine (1)");
            greenpath.hasQuirrel = true;
            greenpath.quirrelData = (23.3f, 107.81f, false, "GREENPATH");
            greenpath.flagData = ("flagSignSE", 17.4f, 3.81f);

            golfScene canyon = new("Fog Canyon", "Fungus3_02", "left1", "Custom Hole");
            canyon.doorColor = new Color(0.7909f, 0.7161f, 1, 0.466f);
            canyon.millibelleSpawn = new Vector3(6, 94.8f, 0.006f);
            canyon.customHoleObject = true;
            canyon.customHolePosition = (new Vector3(6.45f, 5.3f), new Vector3(3.02f, 0.6f));
            canyon.flagData = ("flagSignW", 11.9f, 4.81f);
            
            golfScene edge = new("Kingdom's Edge", "Deepnest_East_11", "right1", "bot1");
            edge.doorColor = new Color(0.6709f, 0.8761f, 1, 0.466f);
            edge.millibelleSpawn = new Vector3(101, 119.8f, 0.006f);
            edge.objectsToDisable.Add("Spawn 1");
            edge.hasQuirrel = true;
            edge.quirrelData = (91.5f, 119.81f, true, "EDGE");
            edge.flagData = ("flagSignSE", 33.1f, 42.81f);

            golfScene waterways = new("Royal Waterways", "Waterways_02", "top1", "bot2");
            waterways.doorColor = new Color(0.1887f, 0.4961f, 0.52f, 0.466f);
            waterways.millibelleSpawn = new Vector3(69, 28.8f, 0.006f);
            waterways.flagData = ("flagSignSE", 216, 3.81f);

            golfScene cliffs = new("Howling Cliffs", "Cliffs_01", "right1", "right3");
            cliffs.doorColor = new Color(0.2509f, 0.3761f, 0.5254f, 0.466f);
            cliffs.millibelleSpawn = new Vector3(133, 143.8f, 0.006f);
            cliffs.flagData = ("flagSignE", 114.6f, 7.81f);

            golfScene abyss = new("The Abyss", "Abyss_06_Core", "top1", "bot1");
            abyss.doorColor = new Color(0, 0, 0, 0.466f);
            abyss.millibelleSpawn = new Vector3(85, 256.8f, 0.006f);
            abyss.childrenToDisable.Add("abyss_door", new List<string> { "Gate", "Collider" });
            abyss.objectsToDisable.Add("floor_closed");
            abyss.objectsToDisable.Add("Shade Sibling Spawner");
            abyss.flagData = ("flagSignSW", 30.6f, 3.81f);

            golfScene fungal = new("Fungal Wastes", "Fungus2_12", "left1", "bot1");
            fungal.doorColor = new Color(0.1509f, 0.3087f, 0.64f, 0.466f);
            fungal.secondaryDoorColor = new Color(0.6709f, 0.6087f, 0, 0.466f);
            fungal.millibelleSpawn = new Vector3(9, 7.8f, 0.006f);
            fungal.flagData = ("flagSignSW", 94.3f, 3.81f);

            golfScene sanctum = new("Soul Sanctum", "Ruins1_30", "left2", "bot1");
            sanctum.doorColor = new Color(0.6709f, 0.7361f, 1, 0.466f);
            sanctum.millibelleSpawn = new Vector3(10, 3.8f, 0.006f);
            sanctum.objectsToDisable.Add("Quake Floor Glass");
            sanctum.objectsToDisable.Add("Quake Floor Glass (1)");
            sanctum.objectsToDisable.Add("Quake Floor Glass (2)");
            sanctum.flagData = ("flagSignSE", 53.5f, 3.81f);

            golfScene basin = new("Ancient Basin", "Abyss_04", "top1", "bot1");
            basin.doorColor = new Color(0.5109f, 0.4761f, 0.4654f, 0.466f);
            basin.millibelleSpawn = new Vector3(49, 83.8f, 0.006f);
            basin.objectsToDisable.Add("black_grass3 (2)");
            basin.flagData = ("flagSignSE", 54.8f, 8.81f);

            golfScene qg = new("Queen's Gardens", "Fungus3_04", "left1", "right2");
            qg.doorColor = new Color(0.4709f, 1, 0.5254f, 0.466f);
            qg.millibelleSpawn = new Vector3(27, 83.8f, 0.006f);
            qg.objectsToDisable.Add("Ruins Lever");
            qg.objectsToDisable.Add("Garden Slide Floor");
            qg.flagData = ("flagSignE", 31.3f, 5.81f);

            golfScene city = new("City of Tears", "Ruins1_03", "right1", "left1");
            city.doorColor = new Color(0.2909f, 0.4561f, 1, 0.466f);
            city.millibelleSpawn = new Vector3(138, 40.8f, 0.006f);
            city.objectsToDisable.Add("Direction Pole Bench");
            city.flagData = ("flagSignW", 5.2f, 8.81f);

            golfScene deepnest = new("Deepnest", "Deepnest_35", "top1", "bot1");
            deepnest.doorColor = new Color(0.1298f, 0.1509f, 0.28f, 0.466f);
            deepnest.millibelleSpawn = new Vector3(41, 103.8f, 0.006f);
            deepnest.flagData = ("flagSignSW", 20.3f, 3.81f);

            golfScene peak = new("Crystal Peak", "Mines_23", "right1", "left1");
            peak.doorColor = new Color(1, 0.6372f, 1, 0.466f);
            peak.millibelleSpawn = new Vector3(170, 27.8f, 0.006f);
            peak.objectsToDisable.Add("brk_barrel_04");
            peak.objectsToDisable.Add("crystal_barrel_03");
            peak.flagData = ("flagSignNW", 8.8f, 9.81f);

            golfScene palace = new("White Palace", "White_Palace_19", "top1", "left1");
            palace.doorColor = new Color(0.85f, 0.85f, 1, 0.466f);
            palace.millibelleSpawn = new Vector3(14, 157.8f, 0.006f);
            palace.objectsToDisable.Add("white_vine_01_silhouette (3)");
            palace.flagData = ("flagSignW", 94.2f, 33.81f);
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
            GameObject[] children = tent.GetComponentsInChildren<GameObject>();
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

        public static void placeDoor(float x, float y, golfScene room) {
            GameObject.Instantiate(prefabs["GG_Atrium"]["GG_big_door_part_small"], new Vector3(x, y, 8.13f), Quaternion.identity).SetActive(true);

            GameObject glow1 = GameObject.Instantiate(prefabs["GG_Atrium"]["Col_Glow_Remasker (1)"], new Vector3(x - 0.6f, y - 4.3f, 11.99f), Quaternion.identity);
            glow1.SetActive(true);
            glow1.GetComponent<SpriteRenderer>().color = room.doorColor;

            GameObject glow2 = GameObject.Instantiate(prefabs["GG_Atrium"]["Col_Glow_Remasker (2)"], new Vector3(x + 1.63f, y - 5.72f, 18.99f), Quaternion.identity);
            glow2.SetActive(true);
            glow2.GetComponent<SpriteRenderer>().color = (room.secondaryDoorColor != Color.black ? room.secondaryDoorColor : room.doorColor);

            GameObject transition = GameObject.Instantiate(prefabs["GG_Atrium"]["Door_Workshop"], new Vector3(x - 0.2f, y - 1.92f, 0.2f), Quaternion.identity);
            TransitionPoint tp = transition.GetComponent<TransitionPoint>();
            string transitionName = "door" + (golfScene.courseList.IndexOf(room.scene) + 1);
            transition.name = tp.name = transitionName;
            transition.SetActive(true);
            PlayMakerFSM doorControlFSM = PlayMakerFSM.FindFsmOnGameObject(transition, "Door Control");
            FsmState changeSceneState = doorControlFSM.GetState("Change Scene");
            BeginSceneTransition enterAction = (BeginSceneTransition)(changeSceneState.GetAction(0));
            enterAction.sceneName = room.scene;
            enterAction.entryGateName = room.startTransition;
            changeSceneState.InsertAction(new setCustomLoad(true), 0);
            changeSceneState.InsertAction(new setGate(transitionName), 1);
            FsmState inRangeState = doorControlFSM.GetState("In Range");
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
                    FsmState choiceState = self.GetState("Choice");
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
            Texture2D testFlagTexture = new Texture2D(1, 1);
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

        private void updateHallScoreboard(GameObject summaryBoard) {
            BossSummaryBoard bsb = summaryBoard.GetComponent<BossSummaryBoard>();
            FieldInfo uiField = typeof(BossSummaryBoard).GetField("ui", BindingFlags.NonPublic | BindingFlags.Instance);
            BossSummaryUI bsu = uiField.GetValue(bsb) as BossSummaryUI;
            bsu.gameObject.FindGameObjectInChildren("Title_Text").SetActive(false);
            GameObject listGrid = bsu.gameObject.FindGameObjectInChildren("List Grid");
            List<GameObject> children = new();
            listGrid.FindAllChildren(children);
            List<GameObject> excessLines = new();
            for(int i = 1; i < 10; i++) {
                children[i].FindGameObjectInChildren("Name_Text").GetComponent<uuiText>().text = golfScene.courseDict[golfScene.courseList[i - 1]].name;
            }
            for(int i = 10; i < 12; i++) {
                excessLines.Add(children[i]);
            }
            for(int i = 12; i < 21; i++) {
                string sceneName = golfScene.courseDict[golfScene.courseList[i - 12]].scene;
                children[i].FindGameObjectInChildren("Name_Text").GetComponent<uuiText>().text = (golfData.scoreboard.ContainsKey(sceneName) ? golfData.scoreboard[sceneName].ToString() : "---");
            }
            uuiText totalText = children[21].FindGameObjectInChildren("Name_Text").GetComponent<uuiText>();
            totalText.text = "Total";
            totalText.alignment = TextAnchor.MiddleRight;
            excessLines.Add(children[22]);
            for(int i = 23; i < 32; i++) {
                children[i].FindGameObjectInChildren("Name_Text").GetComponent<uuiText>().text = golfScene.courseDict[golfScene.courseList[i - 14]].name;
            }
            excessLines.Add(children[33]);
            for(int i = 34; i < 43; i++) {
                string sceneName = golfScene.courseDict[golfScene.courseList[i - 25]].scene;
                children[i].FindGameObjectInChildren("Name_Text").GetComponent<uuiText>().text = (golfData.scoreboard.ContainsKey(sceneName) ? golfData.scoreboard[sceneName].ToString() : "---");
            }
            int total = 0;
            foreach(string scene in golfScene.courseList) {
                if(golfData.scoreboard.ContainsKey(scene)) {
                    total += golfData.scoreboard[scene];
                }
            }
            children[32].FindGameObjectInChildren("Name_Text").GetComponent<uuiText>().text = total.ToString();
            for(int i = 43; i < 45; i++) {
                excessLines.Add(children[i]);
            }
            foreach(GameObject line in excessLines) {
                line.FindGameObjectInChildren("Name_Text").GetComponent<uuiText>().text = "";
            }
            for(int i = 1; i < 45; i++) {
                GameObject image = children[i].FindGameObjectInChildren("Image");
                if(i == 32) {
                    UnityEngine.UI.Image uuiImage = image.GetComponent<UnityEngine.UI.Image>();
                    if(golfData.scoreboard.Count < 18) {
                        uuiImage.sprite = bsu.stateSprites[1];
                    }
                    else if(total <= 250) {
                        uuiImage.sprite = bsu.stateSprites[4];
                        image.transform.localScale = new Vector3(1, 1, 1);
                    }
                    else if(total <= 285) {
                        uuiImage.sprite = bsu.stateSprites[3];
                        image.transform.localScale = new Vector3(1, 1.3f, 1);
                    }
                    else {
                        uuiImage.sprite = bsu.stateSprites[2];
                    image.transform.localScale = new Vector3(1, 1, 1);
                    }
                }
                else {
                    image.SetActive(false);
                }
            }
        }

        private void addDialogue() {
            FieldInfo field = typeof(Language.Language).GetField("currentEntrySheets", BindingFlags.NonPublic | BindingFlags.Static);
            Dictionary<string, Dictionary<string, string>> currentEntrySheets = (Dictionary<string, Dictionary<string, string>>)field.GetValue(null);
            if(currentEntrySheets.ContainsKey("GolfQuirrel")) {
                return;
            }
            Dictionary<string, string> golfQuirrel = new();

            golfQuirrel.Add("HALL", "Welcome to MilliGolf, an 18-hole course and grand tour of Hallownest!<page>You may notice that you have full movement here, but don't worry. All outside progression will be restored when you leave.<page>Your total strokes will be tallied for each course on this scoreboard and will update if you beat your best score.<page>Best of luck and happy golfing!");
            golfQuirrel.Add("DIRTMOUTH", "This is Millibelle the Banker/Thief. She will act as your golf ball and personal punching bag.<page>Try to punt her into the well with as few strokes as possible<page>You may reset or return to the Hall at any time by coming back through this exit or using your dream gate.");
            golfQuirrel.Add("HIVE", "Some courses may prove to be quite tedious for normal nail slashes.<page>In some cases, you may find that a nail art is better suited for the situation.<page>Take a look at each art and observe how their effects differ.");
            golfQuirrel.Add("GREENPATH", "If you ever lose track of the ball, tap your map button to switch between camera modes.");
            golfQuirrel.Add("EDGE", "Please watch your step!<page>This course has some nasty spikes, but you don't need to worry about your health when golfing.");
            
            currentEntrySheets.Add("GolfQuirrel", golfQuirrel);
        }

        public static void disableTransition(GameObject tp) {
            tp.GetComponent<BoxCollider2D>().isTrigger = false;
            TransitionPoint actualTP = tp.GetComponent<TransitionPoint>();
            actualTP.enabled = false;
            if(!actualTP.isADoor) {
                tp.layer = LayerMask.NameToLayer("Terrain");
            }
            else {
                tp.GetComponent<BoxCollider2D>().enabled = false;
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
            areaTitleRef.SetState("Pause");
            areaTitleRef.gameObject.SetActive(true);
        }
    }

    public class golfScene {
        public static List<string> courseList = new();
        public static Dictionary<string, golfScene> courseDict = new();
        public string name, scene, startTransition, holeTarget;
        public Vector3 millibelleSpawn;
        public Color doorColor;
        public Color secondaryDoorColor;
        public bool customHoleObject;
        public (Vector3, Vector3) customHolePosition;
        public bool hasQuirrel;
        public (float, float, bool, string) quirrelData;
        public (string, float, float) flagData;
        public List<string> objectsToDisable = new();
        public Dictionary<string, List<string>> childrenToDisable = new();

        public golfScene(string name, string scene, string transition, string holeTarget) {
            this.name = name;
            this.scene = scene;
            startTransition = transition;
            this.holeTarget = holeTarget;
            courseList.Add(scene);
            courseDict.Add(scene, this);
        }
    }

    public class pbTracker {
        public static bool isActivelyScoring;
        public static bool isPB;
        public static int score;
        public static void update(int score, bool pb) {
            pbTracker.score = score;
            isPB = pb;
            isActivelyScoring = true;
        }
    }

    public class progressionLog {
        static PlayerData pd;
        static bool canDash;
        static bool crossroadsInfected;
        static bool hasAcidArmour;
        static bool hasCyclone;
        static bool hasDashSlash;
        static bool hasDoubleJump;
        static bool hasDreamGate;
        static bool hasDreamNail;
        static bool hasLantern;
        static bool hasMap;
        static bool hasNailArt;
        static bool hasSuperDash;
        static bool hasUpwardSlash;
        static bool hasWalljump;

        public static void logProgression() {
            pd = PlayerData.instance;
            canDash = pd.canDash;
            crossroadsInfected = pd.crossroadsInfected;
            hasAcidArmour = pd.hasAcidArmour;
            hasCyclone = pd.hasCyclone;
            hasDashSlash = pd.hasDashSlash;
            hasDoubleJump = pd.hasDoubleJump;
            hasDreamGate = pd.hasDreamGate;
            hasDreamNail = pd.hasDreamNail;
            hasLantern = pd.hasLantern;
            hasMap = pd.hasMap;
            hasNailArt = pd.hasNailArt;
            hasSuperDash = pd.hasSuperDash;
            hasUpwardSlash = pd.hasUpwardSlash;
            hasWalljump = pd.hasWalljump;
        }

        public static void overrideProgression() {
            pd = PlayerData.instance;
            pd.canDash = true;
            pd.crossroadsInfected = false;
            pd.hasAcidArmour = true;
            pd.hasCyclone = true;
            pd.hasDashSlash = true;
            pd.hasDoubleJump = true;
            pd.hasDreamGate = true;
            pd.hasDreamNail = true;
            pd.hasLantern = false;
            pd.hasMap = true;
            pd.hasNailArt = true;
            pd.hasSuperDash = true;
            pd.hasUpwardSlash = true;
            pd.hasWalljump = true;
        }

        public static void restoreProgression() {
            pd = PlayerData.instance;
            pd.canDash = canDash;
            pd.crossroadsInfected = crossroadsInfected;
            pd.hasAcidArmour = hasAcidArmour;
            pd.hasCyclone = hasCyclone;
            pd.hasDashSlash = hasDashSlash;
            pd.hasDoubleJump = hasDoubleJump;
            pd.hasDreamGate = hasDreamGate;
            pd.hasDreamNail = hasDreamNail;
            pd.hasLantern = hasLantern;
            pd.hasMap = hasMap;
            pd.hasNailArt = hasNailArt;
            pd.hasSuperDash = hasSuperDash;
            pd.hasUpwardSlash = hasUpwardSlash;
            pd.hasWalljump = hasWalljump;
        }
    }

    public class logProgression: FsmStateAction {
        public override void OnEnter() {
            progressionLog.logProgression();
            Finish();
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
            if(fsmGo!=null && !string.IsNullOrEmpty(fsmGo.name)) {
                damager = fsmGo.name;
            }
            else {
                damager = "";
            }
            if(!string.IsNullOrEmpty(damager)) {
                MilliGolf.tinkDamager = damager;
                if(new List<string> { "Slash", "AltSlash", "UpSlash", "DownSlash", "Great Slash", "Dash Slash", "Hit L", "Hit R" }.Contains(damager)) {
                    MilliGolf.currentScore++;
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
            speedMax = speedMin = speed;
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
                MilliGolf.ballCam = !MilliGolf.ballCam;
                if(MilliGolf.ballCam) {
                    CameraMods.lockZoneList.Clear();
                    while(GameCameras.instance.cameraController.lockZoneList.Count > 0) {
                        CameraLockArea currentZone = typeof(CameraController).GetField("currentLockArea", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(GameCameras.instance.cameraController) as CameraLockArea;
                        CameraMods.lockZoneList.Insert(0, currentZone);
                        GameCameras.instance.cameraController.ReleaseLock(currentZone);
                    }
                    typeof(CameraTarget).GetField("heroTransform", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(GameCameras.instance.cameraTarget, MilliGolf.millibelleRef.transform);
                }
                else {
                    while(CameraMods.lockZoneList.Count > 0) {
                        GameCameras.instance.cameraController.LockToArea(CameraMods.lockZoneList[0]);
                        CameraMods.lockZoneList.RemoveAt(0);
                    }
                    typeof(CameraTarget).GetField("heroTransform", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(GameCameras.instance.cameraTarget, HeroController.instance.transform);
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

    public class isInGolfHallBool: FsmStateAction {
        public FsmEvent isTrue;
        public override void OnEnter() {
            if(MilliGolf.isInGolfRoom && GameManager.instance.sceneName == "GG_Workshop") {
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
                areaTitleVars.GetFsmBool("Title Has Superscript").Value = (pbTracker.isPB ? true : false);
            }
            pbTracker.isActivelyScoring = false;
            Finish();
        }
    }

    public class collisionDetector: MonoBehaviour {
        void OnCollisionEnter2D(Collision2D collision) {
            MilliGolf.OnCollisionEnter2D(collision);
        }
    }

    public class LocalGolfSettings {
        public Dictionary<string, int> scoreboard = new();
    }
}