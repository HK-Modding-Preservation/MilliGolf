using System;
using System.Collections.Generic;
using GlobalEnums;
using HutongGames.PlayMaker;
using ItemChanger;
using ItemChanger.Modules;
using MilliGolf.Rando.Interop;
using MilliGolf.Rando.Settings;
using Modding;
using Newtonsoft.Json;
using RandomizerMod.IC;
using RandomizerMod.Logging;
using Satchel;
using UnityEngine;

namespace MilliGolf.Rando.Manager
{
    internal static class GolfManager
    {
        public static List<(string, string)> courseList = new() {
            ("Dirtmouth", "Town"),
            ("Crossroads", "Crossroads_07"),
            ("Grounds", "RestingGrounds_05"),
            ("Hive", "Hive_03"),
            ("Greenpath", "Fungus1_31"),
            ("Canyon", "Fungus3_02"),
            ("Edge", "Deepnest_East_11"),
            ("Waterways", "Waterways_02"),
            ("Cliffs", "Cliffs_01"),
            ("Abyss", "Abyss_06_Core"),
            ("Fungal", "Fungus2_12"),
            ("Sanctum", "Ruins1_30"),
            ("Basin", "Abyss_04"),
            ("Gardens", "Fungus3_04"),
            ("City", "Ruins1_03"),
            ("Deepnest", "Deepnest_35"),
            ("Peak", "Mines_23"),
            ("Palace", "White_Palace_19")
        };        
        public static GolfRandoSettings GlobalSettings => MilliGolf.GS;
        public static LocalGolfSettings SaveSettings => MilliGolf.golfData;

        public static void Hook()
        {
            LogicHandler.Hook();
            ItemHandler.Hook();
            TransitionHandler.Hook();
            ConnectionMenu.Hook();
            SettingsLog.AfterLogSettings += AddFileSettings;

            if(ModHooks.GetMod("ConnectionSettingsRando") is Mod)
                CSRInterop.Hook();
        }

        public static bool IsRandoSave()
        {
            try
            {
                RandomizerModule module = ItemChangerMod.Modules.Get<RandomizerModule>();
                return module is not null;
            }
            catch (NullReferenceException)
            {
                return false;
            }
        }

        public static void AddICHooks()
        {
            Events.OnEnterGame += ICHooks;
            Events.AfterStartNewGame += ICHooks;
        }
        private static void AddFileSettings(LogArguments args, System.IO.TextWriter tw)
        {
            // Log settings into the settings file
            tw.WriteLine("MilliGolf Settings:");
            using JsonTextWriter jtw = new(tw) { CloseOutput = false };
            RandomizerMod.RandomizerData.JsonUtil._js.Serialize(jtw, GlobalSettings);
            tw.WriteLine();
        }

        private static void ICHooks()
        {
            SplitNail splitNail = ItemChangerMod.Modules.Get<SplitNail>();
            SplitCloak splitDash = ItemChangerMod.Modules.Get<SplitCloak>();
            SwimSkill swim = ItemChangerMod.Modules.Get<SwimSkill>();

            On.HeroController.CanAttack -= NailOverride;
            On.HeroController.DoAttack -= NailOverride2;
            On.HeroController.CanDash -= DashOverride;
            Events.RemoveFsmEdit(new("Surface Water Region"), SwimOverride);
            
            if (splitNail is not null)
            {
                On.HeroController.CanAttack += NailOverride;
                On.HeroController.DoAttack += NailOverride2; // Yes, it does need to get THIS hacky.
            }
            if (splitDash is not null)
            {
                On.HeroController.CanDash += DashOverride;
            }
            if (swim is not null)
            {
                Events.AddFsmEdit(new("Surface Water Region"), SwimOverride);
            }
        }

        private static bool NailOverride(On.HeroController.orig_CanAttack orig, HeroController self)
        {
            bool boolLessNail = (
                ReflectionHelper.GetField<HeroController, float>(self, "attack_cooldown") <= 0f && 
                !self.cState.attacking && 
                !self.cState.dashing && 
                !self.cState.dead && 
                !self.cState.hazardDeath && 
                !self.cState.hazardRespawning && 
                !self.controlReqlinquished && 
                self.hero_state != ActorStates.no_input && 
                self.hero_state != ActorStates.hard_landing && 
                self.hero_state != ActorStates.dash_landing
                );
            return orig(self) || (boolLessNail && MilliGolf.isInGolfRoom);
        }

        private static void NailOverride2(On.HeroController.orig_DoAttack orig, HeroController self)
        {
            if (MilliGolf.isInGolfRoom)
            {
                self.Attack(GetAttackDirection(self));
            }
            else
            {
                orig(self);
            }
        }

        private static AttackDirection GetAttackDirection(HeroController hc)
        {
            if (hc.wallSlidingL)
            {
                return AttackDirection.normal;
            }
            else if (hc.wallSlidingR)
            {
                return AttackDirection.normal;
            }

            if (hc.vertical_input > Mathf.Epsilon)
            {
                return AttackDirection.upward;
            }
            else if (hc.vertical_input < -Mathf.Epsilon)
            {
                if (hc.hero_state != ActorStates.idle && hc.hero_state != ActorStates.running)
                {
                    return AttackDirection.downward;
                }
                else
                {
                    return AttackDirection.normal;
                }
            }
            else
            {
                return AttackDirection.normal;
            }
        }

        private static void SwimOverride(PlayMakerFSM fsm)
        {
            if (fsm.gameObject.LocateMyFSM("Acid Armour Check") != null) return;
            
            isGolfingBool golfSwim = new();
            golfSwim.isTrue = FsmEvent.GetFsmEvent("GOLFING");
            fsm.AddState("Is Golfing?");
            fsm.AddTransition("Is Golfing?", "GOLFING", "Big Splash?");
            fsm.AddTransition("Is Golfing?", "FINISHED", "Damage Hero");
            fsm.AddAction("Is Golfing?", golfSwim);
            fsm.ChangeTransition("Check Swim", "DAMAGE", "Is Golfing?");
        }

        private static bool DashOverride(On.HeroController.orig_CanDash orig, HeroController self)
        {
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
            return orig(self) || (boolessDash && MilliGolf.isInGolfRoom);
        }
    }
}