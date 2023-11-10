using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValkyrieFlyMount
{
    [BepInPlugin(pluginID, pluginName, pluginVersion)]
    public class ValkyrieFlyMount : BaseUnityPlugin
    {
        const string pluginID = "shudnal.ValkyrieFlyMount";
        const string pluginName = "Valkyrie Fly Mount";
        const string pluginVersion = "1.0.4";

        private Harmony _harmony;

        private static ConfigEntry<bool> modEnabled;
        private static ConfigEntry<bool> loggingEnabled;
        private static ConfigEntry<KeyboardShortcut> mountShortcut;
        private static ConfigEntry<KeyboardShortcut> dismountShortcut;

        internal static ValkyrieFlyMount instance;
        internal static Valkyrie controlledValkyrie;

        private static bool isFlyingMountValkyrie = false;
        internal static bool playerDropped = false;
        internal static bool castSlowFall;
        internal static float shiftDownTime;
        internal static bool shiftStaminaDepleted;

        internal static DateTime flightStarted;
        internal static bool crosshairState;

        private void Awake()
        {
            if (IsDedicated())
            {
                instance.Logger.LogWarning("Dedicated server. Loading skipped.");
                return;
            }

            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), pluginID);

            instance = this;

            ConfigInit();
        }

        private void OnDestroy()
        {
            Config.Save();
            _harmony?.UnpatchSelf();
        }

        public static void LogInfo(object data)
        {
            if (loggingEnabled.Value)
                instance.Logger.LogInfo(data);
        }

        private void ConfigInit()
        {
            Config.Bind("General", "NexusID", 2520, "Nexus mod ID for updates");

            modEnabled = Config.Bind("General", "Enabled", defaultValue: true, "Enable the mod.");
            loggingEnabled = Config.Bind("General", "Logging enabled", defaultValue: true, "Enable logging.");
            mountShortcut = Config.Bind("General", "Mount shortcut", defaultValue: new KeyboardShortcut(KeyCode.T, new KeyCode[1] { KeyCode.LeftShift }), "Mount shortcut.");
            dismountShortcut = Config.Bind("General", "Disount shortcut", defaultValue: new KeyboardShortcut(KeyCode.E, new KeyCode[1] { KeyCode.LeftShift }), "Dismount shortcut.");
        }

        public void LateUpdate()
        {
            if (!modEnabled.Value) return;

            if (Player.m_localPlayer == null) return;

            if (mountShortcut.Value.IsDown() || 
                ZInput.GetButton("AltPlace") && ZInput.GetButton("Crouch") && ZInput.GetButton("Jump") ||
                ZInput.GetButton("JoyAltPlace") && ZInput.GetButton("JoyCrouch") && ZInput.GetButton("JoyJump"))
            {
                SpawnValkyrie(Player.m_localPlayer);
            }
        }
        
        public static bool CanOperateValkyrie()
        {
            Player localPlayer = Player.m_localPlayer;

            return !(localPlayer == null || localPlayer.IsDead() || localPlayer.InCutscene() || localPlayer.IsTeleporting()) &&
                    (Chat.instance == null || !Chat.instance.HasFocus()) &&
                    !Console.IsVisible() && !Menu.IsVisible() && TextViewer.instance != null &&
                    !TextViewer.instance.IsVisible() && !TextInput.IsVisible() &&
                    !Minimap.IsOpen() && !GameCamera.InFreeFly() && !StoreGui.IsVisible() && !InventoryGui.IsVisible();
        }

        public static bool IsDedicated()
        {
            var method = typeof(ZNet).GetMethod(nameof(ZNet.IsDedicated), BindingFlags.Public | BindingFlags.Instance);
            var openDelegate = (Func<ZNet, bool>)Delegate.CreateDelegate(typeof(Func<ZNet, bool>), method);
            return openDelegate(null);
        }

        private void SpawnValkyrie(Player player)
        {
            if (isFlyingMountValkyrie)
                return;

            if (controlledValkyrie != null)
            {
                player.Message(MessageHud.MessageType.Center, Localization.instance.Localize("$hud_powernotready"));
                return;
            }

            if (!CanOperateValkyrie() || player.IsAttachedToShip() || player.IsAttached() || player.IsDead() || player.IsRiding() || player.IsSleeping() || 
                                       player.IsTeleporting() || player.InPlaceMode() || player.InBed() || player.InCutscene() || player.InInterior())
            {
                return;
            }

            isFlyingMountValkyrie = true;
            flightStarted = DateTime.Now;

            if (player.m_nview != null && player.m_nview.IsValid())
            {
                player.Heal((player.GetMaxHealth() - player.GetHealth()) * 0.3f);
                player.AddStamina(player.GetMaxStamina());
                player.SetCrouch(false);
            }

            crosshairState = Hud.instance.m_crosshair.enabled;
            Hud.instance.m_crosshair.enabled = false;

            controlledValkyrie = Instantiate(ZNetScene.instance.GetPrefab("Valkyrie")).GetComponent<Valkyrie>();
        }

        [HarmonyPatch(typeof(Menu), nameof(Menu.OnSkip))]
        public static class Menu_OnSkip_IntroSkip
        {
            private static void Prefix()
            {
                if ((bool)controlledValkyrie)
                {
                    controlledValkyrie.DropPlayer(destroy: true);
                }
            }
        }

        [HarmonyPatch(typeof(Valkyrie), nameof(Valkyrie.Awake))]
        public static class Valkyrie_Awake_MountInitialization
        {
            private static bool Prefix(Valkyrie __instance)
            {
                if (!modEnabled.Value) return true;

                __instance.m_nview = __instance.GetComponent<ZNetView>();
                __instance.m_animator = __instance.GetComponentInChildren<Animator>();
                if (!__instance.m_nview.IsOwner())
                {
                    __instance.enabled = false;
                    return false;
                }

                if (Player.m_localPlayer.m_firstSpawn) return true;

                if (!isFlyingMountValkyrie) return true;

                if (!__instance.gameObject.TryGetComponent<Rigidbody>(out _))
                {
                    __instance.gameObject.AddComponent<Rigidbody>().useGravity = false;
                }

                __instance.m_startPause = 0f;
                __instance.m_startAltitude = 10f;
                __instance.m_textDuration = 0f;
                __instance.m_descentAltitude = 100f;
                __instance.m_attachOffset = new Vector3(-0.1f, 1.5f, 0.1f);
                __instance.m_speed = 10f;
                __instance.m_turnRate = 5f;
                __instance.m_dropHeight = 10f;

                Vector3 position = Player.m_localPlayer.transform.position;
                position.y += __instance.m_startAltitude;

                __instance.transform.position = position;

                Player.m_localPlayer.m_intro = true;

                __instance.m_targetPoint = new Vector3(0, ZoneSystem.instance.GetGroundHeight(Vector3.zero) + 150f, 0);
                if (ZoneSystem.instance.FindClosestLocation("StartTemple", Vector3.zero, out ZoneSystem.LocationInstance location))
                {
                    __instance.m_targetPoint = location.m_position;
                }

                float f = UnityEngine.Random.value * (float)Math.PI * 2f;
                Vector3 vector = new Vector3(Mathf.Sin(f), 0f, Mathf.Cos(f));
                Vector3 a = Vector3.Cross(vector, Vector3.up);
                __instance.m_descentStart = __instance.m_targetPoint + vector * __instance.m_startDescentDistance + a * 200f;
                __instance.m_descentStart.y = __instance.m_descentAltitude;

                Vector3 a2 = __instance.m_targetPoint - __instance.m_descentStart;
                a2.y = 0f;
                a2.Normalize();
                __instance.m_flyAwayPoint = __instance.m_targetPoint + a2 * __instance.m_startDescentDistance;
                __instance.m_flyAwayPoint.y = __instance.m_startAltitude;

                __instance.SyncPlayer(doNetworkSync: true);

                LogInfo("Setting up valkyrie " + __instance.transform.position.ToString() + "   " + ZNet.instance.GetReferencePosition().ToString());

                return false;
            }
        }

        [HarmonyPatch(typeof(Valkyrie), nameof(Valkyrie.LateUpdate))]
        public static class Valkyrie_LateUpdate_DismountCommand
        {
            private static void Prefix(Valkyrie __instance)
            {
                if (!modEnabled.Value) return;

                if (__instance != controlledValkyrie) return;

                if (isFlyingMountValkyrie && (mountShortcut.Value.IsDown() ||
                                              dismountShortcut.Value.IsDown() || 
                                              ZInput.GetButton("Use") && ZInput.GetButton("AltPlace") || 
                                              ZInput.GetButton("JoyUse") && ZInput.GetButton("JoyAltPlace")))
                {
                    __instance.DropPlayer();
                }
            }
        }

        [HarmonyPatch(typeof(Valkyrie), nameof(Valkyrie.UpdateValkyrie))]
        public static class Valkyrie_UpdateValkyrie_MountControl
        {
            private static bool Prefix(Valkyrie __instance, float dt)
            {
                if (!modEnabled.Value) return true;

                if (__instance != controlledValkyrie) return true;

                if (!isFlyingMountValkyrie)
                    return true;

                if (!__instance.TryGetComponent<Rigidbody>(out Rigidbody rigidbody))
                    return true;

                if ((DateTime.Now - ZInput.instance.GetLastInputTimer()).TotalSeconds > 60)
                {
                    // Attempt to travel to spawn point on inactivity
                    return true;
                }

                Vector3 offset = Vector3.zero;

                Vector3 forward = __instance.transform.forward;

                if (CanOperateValkyrie())
                {
                    bool movingForward = (ZInput.GetButton("Forward") || ZInput.GetJoyLeftStickY() < 0f);
                    bool movingBackward = (ZInput.GetButton("Backward") || ZInput.GetJoyLeftStickY() > 0f);

                    if (movingForward && !movingBackward)
                        offset += forward;
                    else if (!movingForward)
                        offset += forward / 5;
                    if (movingBackward && !movingForward)
                        offset -= forward / 5;
                    if (ZInput.GetButton("Left") || ZInput.GetJoyLeftStickX() < 0f)
                        offset -= __instance.transform.right / (movingForward ? 2 : (movingBackward ? 10 : 5));
                    if (ZInput.GetButton("Right") || ZInput.GetJoyLeftStickX() > 0f)
                        offset += __instance.transform.right / (movingForward ? 2 : (movingBackward ? 10 : 5));
                    if ((ZInput.GetButton("Jump") || ZInput.GetButton("JoyRTrigger")))
                        offset.y += (movingForward ? 0.8f : (movingBackward ? 0.2f : 0.5f));
                    if (ZInput.GetButton("Crouch") || ZInput.GetButton("JoyLTrigger"))
                        offset.y -= (movingForward ? 0.8f : (movingBackward ? 0.2f : 0.5f));
                    if (offset.magnitude > 1.0f)
                        offset.Normalize();
                }
                else
                {
                    offset += forward / 5;
                }

                shiftStaminaDepleted = shiftStaminaDepleted || !Player.m_localPlayer.HaveStamina();

                bool nitro = (ZInput.GetButton("Run") || ZInput.GetButton("JoyRun"));
                if (nitro)
                    shiftDownTime += dt;
                else
                {
                    shiftDownTime = 0f;
                    shiftStaminaDepleted = false;
                }

                float shift = 15f * (nitro && !shiftStaminaDepleted ? 1.5f + Mathf.Min(1f, shiftDownTime / 10) : 1);

                Vector3 force = Vector3.Lerp(Vector3.zero, offset * shift, 1f) - rigidbody.velocity;

                if (force.magnitude > 15f)
                    force = force.normalized * 15f;

                rigidbody.AddForce(force, ForceMode.VelocityChange);

                GameCamera.m_instance.GetCameraPosition(dt, out _, out Quaternion rot);

                Quaternion quaternion = Quaternion.RotateTowards(__instance.transform.rotation, rot, __instance.m_turnRate * 15f * dt);
                
                __instance.transform.rotation = quaternion;

                return false;
            }

            private static void Postfix(Valkyrie __instance)
            {
                if (!modEnabled.Value) return;

                if (__instance != controlledValkyrie) return;

                Vector3 pos = __instance.transform.position;
                if (ZoneSystem.instance.GetGroundHeight(pos, out float height2))
                {
                    pos.y = Mathf.Min(Mathf.Max(pos.y, Mathf.Max(height2, ZoneSystem.instance.m_waterLevel) + 5f), 1000f);
                    __instance.transform.position = pos;
                }
            }
        }
        
        [HarmonyPatch(typeof(Valkyrie), nameof(Valkyrie.DropPlayer))]
        public static class Valkyrie_DropPlayer_Dismount
        {
            private static void Postfix(Valkyrie __instance)
            {
                if (!modEnabled.Value) return;

                if (__instance != controlledValkyrie) return;

                if (isFlyingMountValkyrie)
                {
                    isFlyingMountValkyrie = false;

                    playerDropped = true;
                    if (!Player.m_localPlayer.m_seman.HaveStatusEffect("SlowFall"))
                    {
                        castSlowFall = true;
                        Player.m_localPlayer.m_seman.AddStatusEffect("SlowFall".GetStableHashCode());
                        LogInfo("Cast slow fall");
                    }

                    Hud.instance.m_crosshair.enabled = crosshairState;

                    Vector3 forward = Player.m_localPlayer.transform.forward;
                    forward.y = 0f;

                    __instance.m_flyAwayPoint = Player.m_localPlayer.transform.position + forward * 300f;
                    __instance.m_flyAwayPoint.y = Mathf.Max(ZoneSystem.instance.GetGroundHeight(__instance.m_flyAwayPoint), ZoneSystem.instance.m_waterLevel) + Mathf.Max(Player.m_localPlayer.transform.position.y + 100f, 150f);
                    __instance.m_speed = 15f;

                    controlledValkyrie = null;
                }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.InCutscene))]
        public static class Player_InCutscene_Taxi
        {
            private static void Postfix(Player __instance, ref bool __result)
            {
                if (!modEnabled.Value)
                    return;

                if (Player.m_localPlayer != __instance)
                    return;

                if (!isFlyingMountValkyrie)
                    return;

                __result = false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.CheckRun))]
        public static class Player_CheckRun_Taxi
        {
            private static void Prefix(Player __instance, ref float __state)
            {
                if (!modEnabled.Value)
                    return;

                if (Player.m_localPlayer != __instance)
                    return;

                if (!isFlyingMountValkyrie)
                    return;

                __state = Game.m_moveStaminaRate;

                Game.m_moveStaminaRate *= 2f;
            }
            private static void Postfix(Player __instance, ref float __state)
            {
                if (!modEnabled.Value)
                    return;

                if (Player.m_localPlayer != __instance)
                    return;

                if (!isFlyingMountValkyrie)
                    return;

                Game.m_moveStaminaRate = __state;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.Jump))]
        public static class Character_Jump_Taxi
        {
            private static void Prefix(Character __instance, ref float __state)
            {
                if (!modEnabled.Value)
                    return;

                if (Player.m_localPlayer != __instance)
                    return;

                if (!isFlyingMountValkyrie)
                    return;

                __state = __instance.m_jumpStaminaUsage;

                __instance.m_jumpStaminaUsage = 0;
            }

            private static void Postfix(Character __instance, ref float __state)
            {
                if (!modEnabled.Value)
                    return;

                if (Player.m_localPlayer != __instance)
                    return;

                if (!isFlyingMountValkyrie)
                    return;

                __instance.m_jumpStaminaUsage = __state;
            }
        }
        
        [HarmonyPatch(typeof(Player), nameof(Player.Update))]
        public static class Player_Update_Taxi
        {
            private static void Postfix(Player __instance)
            {
                if (!modEnabled.Value) 
                    return;

                if (Player.m_localPlayer != __instance)
                    return;

                if (isFlyingMountValkyrie)
                    return;

                if (playerDropped && castSlowFall && __instance.IsOnGround())
                {
                    castSlowFall = false;
                    playerDropped = false;
                    if (__instance.m_seman.HaveStatusEffect("SlowFall"))
                        __instance.m_seman.RemoveStatusEffect("SlowFall".GetStableHashCode(), true);
                    LogInfo("Remove slow fall");
                }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.UpdateBiome))]
        public static class Player_UpdateBiome_PreserveBiomeCalculation
        {
            private static void Prefix(ref Player __instance)
            {
                if (!modEnabled.Value)
                    return;

                if (Player.m_localPlayer != __instance)
                    return;

                if (!isFlyingMountValkyrie)
                    return;

                if (!__instance.InIntro())
                    return;

                __instance.m_intro = false;
            }

            private static void Postfix(ref Player __instance)
            {
                if (!modEnabled.Value)
                    return;

                if (Player.m_localPlayer != __instance)
                    return;

                if (!isFlyingMountValkyrie)
                    return;

                if (__instance.InIntro())
                    return;

                __instance.m_intro = true;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.UpdateStats), new[] { typeof(float) })]
        public static class Player_UpdateStats_PreserveStatsCalculation
        {
            private static void Prefix(ref Player __instance)
            {
                if (!modEnabled.Value)
                    return;

                if (Player.m_localPlayer != __instance)
                    return;

                if (!isFlyingMountValkyrie)
                    return;

                if (!__instance.InIntro())
                    return;

                __instance.m_intro = false;
            }

            private static void Postfix(ref Player __instance)
            {
                if (!modEnabled.Value)
                    return;

                if (Player.m_localPlayer != __instance)
                    return;

                if (!isFlyingMountValkyrie)
                    return;

                if (__instance.InIntro())
                    return;

                __instance.m_intro = true;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        public static class Character_Damage_PlayerProtection
        {
            private static void Prefix(Character __instance, ref HitData hit, ZNetView ___m_nview)
            {
                if (!modEnabled.Value) return;

                if (___m_nview == null)
                    return;

                if (!isFlyingMountValkyrie)
                    return;

                if (!__instance.IsPlayer())
                    return;

                if (__instance != Player.m_localPlayer)
                    return;

                if (hit.HaveAttacker() && hit.GetAttacker().IsBoss())
                    return;

                hit.m_damage.Modify(Math.Max(0.1f, 0));
            }
        }
        
        [HarmonyPatch(typeof(Hud), nameof(Hud.UpdateCrosshair))]
        [HarmonyPriority(Priority.Last)]
        public static class Hud_UpdateCrosshair_ShowBowCrosshair
        {
            private static void Prefix(Hud __instance, Player player)
            {
                if (!modEnabled.Value) return;

                if (!isFlyingMountValkyrie)
                    return;

                if (crosshairState)
                {
                    bool showBowCrosshair = player.GetLeftItem() != null && player.GetLeftItem().m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow;
                    if (Hud.instance.m_crosshair.enabled != showBowCrosshair)
                    {
                        Hud.instance.m_crosshair.enabled = showBowCrosshair;
                    }
                }
            }
        }

        [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.GetEnvironmentOverride))]
        public static class EnvMan_GetEnvironmentOverride_EnableWeatherOnFlight
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(ref Player __state)
            {
                if (!modEnabled.Value) return;

                if (!isFlyingMountValkyrie)
                    return;

                __state = Player.m_localPlayer;

                Player.m_localPlayer = null;
            }

            [HarmonyPriority(Priority.First)]
            private static void Postfix(Player __state)
            {
                if (!modEnabled.Value) return;

                if (!isFlyingMountValkyrie)
                    return;

                Player.m_localPlayer = __state;
            }
        }

    }
}