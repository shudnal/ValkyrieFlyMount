using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValkyrieFlyMount
{
    [BepInPlugin(pluginID, pluginName, pluginVersion)]
    public class ValkyrieFlyMount : BaseUnityPlugin
    {
        const string pluginID = "shudnal.ValkyrieFlyMount";
        const string pluginName = "Valkyrie Fly Mount";
        const string pluginVersion = "1.0.8";

        private readonly Harmony harmony = new Harmony(pluginID);

        private static ConfigEntry<bool> modEnabled;
        private static ConfigEntry<bool> loggingEnabled;
        private static ConfigEntry<KeyboardShortcut> mountShortcut;
        private static ConfigEntry<KeyboardShortcut> dismountShortcut;
        
        private static ConfigEntry<KeyboardShortcut> hoverShortcut;
        private static ConfigEntry<bool> hoveringStoppedByMoving;

        private static ConfigEntry<float> maxAltitude;
        private static ConfigEntry<float> forceMultiplier;
        private static ConfigEntry<float> rotationMultiplier;
        private static ConfigEntry<float> verticalAccelerationMultiplier;
        private static ConfigEntry<float> horizontalAccelerationMultiplier;
        private static ConfigEntry<float> boostAccelerationMultiplier;
        private static ConfigEntry<float> forwardAccelerationMultiplier;

        internal static ValkyrieFlyMount instance;
        internal static Valkyrie controlledValkyrie;

        private static bool isFlyingMountValkyrie = false;
        internal static bool playerDropped = false;
        internal static bool castSlowFall;
        internal static float accelerationMultiplier;
        internal static bool accelerationStaminaDepleted;
        internal static bool isHovering = false;

        internal static bool crosshairState;

        private static readonly int slowFallHash = "SlowFall".GetStableHashCode();

        private static float currentForce = 0f;

        private void Awake()
        {
            harmony.PatchAll();

            instance = this;

            ConfigInit();

            Game.isModded = true;
        }

        private void OnDestroy()
        {
            Config.Save();
            instance = null;
            harmony?.UnpatchSelf();
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
            loggingEnabled = Config.Bind("General", "Logging enabled", defaultValue: false, "Enable logging.");
            mountShortcut = Config.Bind("General", "Mount shortcut", defaultValue: new KeyboardShortcut(KeyCode.T, new KeyCode[1] { KeyCode.LeftShift }), "Mount shortcut.");
            dismountShortcut = Config.Bind("General", "Disount shortcut", defaultValue: new KeyboardShortcut(KeyCode.E, new KeyCode[1] { KeyCode.LeftShift }), "Dismount shortcut.");
            
            hoverShortcut = Config.Bind("Hovering", "Hovering shortcut", defaultValue: new KeyboardShortcut(KeyCode.T), "Hovering shortcut. Press while on valkyrie to stop moving forward automatically.");
            hoveringStoppedByMoving = Config.Bind("Hovering", "Hovering stopped by moving", defaultValue: true, "If true - moving valkyrie will resume flying forward." +
                                                                                                      "\nIf false - only pressing hovering shortcut again will start valkyrie flying forward automatically.");

            maxAltitude = Config.Bind("Misc", "Maximum altitude", defaultValue: 1500f, "Height limit.");
            forceMultiplier = Config.Bind("Misc", "Force multiplier", defaultValue: 1f, "Multiplier of force applied to move Valkyrie. Basically indirect speed multiplier");
            rotationMultiplier = Config.Bind("Misc", "Rotation multiplier", defaultValue: 1f, "Multiplier of rotation delta. Basically angular velocity");
            verticalAccelerationMultiplier = Config.Bind("Misc", "Vertical Acceleration multiplier", defaultValue: 1f, "Multiplier of vertical change of speed on Up and Down movement");
            horizontalAccelerationMultiplier = Config.Bind("Misc", "Horizontal Acceleration multiplier", defaultValue: 1f, "Multiplier of horizontal change of speed on Left or Right movement");
            boostAccelerationMultiplier = Config.Bind("Misc", "Boost Acceleration multiplier", defaultValue: 1f, "Multiplier of speed of boost speed increase");
            forwardAccelerationMultiplier = Config.Bind("Misc", "Forward acceleration multiplier", defaultValue: 1f, "Multiplier of speed of regular movement speed increase");
        }

        public void LateUpdate()
        {
            if (!modEnabled.Value || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return;

            if (Player.m_localPlayer == null)
                return;

            if (mountShortcut.Value.IsDown() || ZInput.GetButton("AltPlace") && ZInput.GetButton("Crouch") && ZInput.GetButton("Jump") ||
                                                ZInput.GetButton("JoyAltPlace") && ZInput.GetButton("JoyCrouch") && ZInput.GetButton("JoyJump"))
                SpawnValkyrie(Player.m_localPlayer);
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

            if (player.m_nview != null && player.m_nview.IsValid())
            {
                player.Heal((player.GetMaxHealth() - player.GetHealth()) * 0.3f);
                player.AddStamina(player.GetMaxStamina());
                player.SetCrouch(false);
            }

            crosshairState = Hud.instance.m_crosshair.enabled;
            Hud.instance.m_crosshair.enabled = false;

            currentForce = 0f;
            isHovering = false;

            controlledValkyrie = Instantiate(ZNetScene.instance.GetPrefab("Valkyrie")).GetComponent<Valkyrie>();
        }

        [HarmonyPatch(typeof(Menu), nameof(Menu.OnSkip))]
        public static class Menu_OnSkip_IntroSkip
        {
            private static bool Prefix(Menu __instance)
            {
                if (!(bool)controlledValkyrie)
                    return true;

                __instance.Hide();
                controlledValkyrie.DropPlayer(destroy: false);
                return false;
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

                if (!isFlyingMountValkyrie)
                    return true;

                if (!__instance.gameObject.TryGetComponent<Rigidbody>(out _))
                    __instance.gameObject.AddComponent<Rigidbody>().useGravity = false;

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
               
                GameCamera.m_instance.GetCameraPosition(Time.unscaledDeltaTime, out _, out Quaternion rot);
                __instance.transform.rotation = rot;

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
            private static void Postfix(Valkyrie __instance)
            {
                if (!modEnabled.Value)
                    return;

                if (__instance != controlledValkyrie)
                    return;

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
                if (!modEnabled.Value)
                    return true;

                if (TextViewer.IsShowingIntro())
                    return true;

                if (__instance != controlledValkyrie)
                    return true;

                if (!isFlyingMountValkyrie)
                    return true;

                if (!__instance.TryGetComponent(out Rigidbody rigidbody))
                    return true;

                if (!isHovering && (DateTime.Now - ZInput.instance.GetLastInputTimer()).TotalSeconds > 60)
                {
                    // Attempt to travel to spawn point on inactivity
                    return true;
                }

                isHovering ^= hoverShortcut.Value.IsDown();

                Vector3 forward = __instance.transform.forward;

                Vector3 offset = isHovering ? Vector3.zero : forward / 4;
                
                currentForce = Mathf.MoveTowards(currentForce, 0, dt);

                if (CanOperateValkyrie())
                {
                    bool movingForward = (ZInput.GetButton("Forward") || ZInput.GetJoyLeftStickY() < 0f);
                    bool movingBackward = (ZInput.GetButton("Backward") || ZInput.GetJoyLeftStickY() > 0f);

                    if (movingForward && !movingBackward) // Moving forward
                        currentForce = Mathf.MoveTowards(currentForce, 1, dt * 3 * forwardAccelerationMultiplier.Value);
                    else if (movingBackward && !movingForward) // Moving backward
                        offset -= forward / 4;

                    if (ZInput.GetButton("Left") || ZInput.GetJoyLeftStickX() < 0f)
                        offset -= __instance.transform.right / (movingForward ? 2 : (movingBackward ? 10 : 5)) * horizontalAccelerationMultiplier.Value;

                    if (ZInput.GetButton("Right") || ZInput.GetJoyLeftStickX() > 0f)
                        offset += __instance.transform.right / (movingForward ? 2 : (movingBackward ? 10 : 5)) * horizontalAccelerationMultiplier.Value;

                    if ((ZInput.GetButton("Jump") || ZInput.GetButton("JoyRTrigger")))
                        offset.y += (movingForward ? 0.8f : (movingBackward ? 0.2f : 0.5f)) * verticalAccelerationMultiplier.Value;

                    if (ZInput.GetButton("Crouch") || ZInput.GetButton("JoyLTrigger"))
                        offset.y -= (movingForward ? 0.8f : (movingBackward ? 0.2f : 0.5f)) * verticalAccelerationMultiplier.Value;

                    if (hoveringStoppedByMoving.Value && (movingForward || movingBackward))
                        isHovering = false;
                }

                offset += forward * currentForce;

                if (offset.magnitude > 1.0f)
                    offset.Normalize();

                accelerationStaminaDepleted = accelerationStaminaDepleted || !Player.m_localPlayer.HaveStamina();

                bool boost = (ZInput.GetButton("Run") || ZInput.GetButton("JoyRun"));
                if (boost)
                    accelerationMultiplier = Mathf.MoveTowards(accelerationMultiplier, 1f, boostAccelerationMultiplier.Value * dt / 10);
                else
                {
                    accelerationMultiplier = Mathf.MoveTowards(accelerationMultiplier, 0f, dt);
                    accelerationStaminaDepleted = false;
                }

                float acceleration = boost && !accelerationStaminaDepleted ? 1.5f + accelerationMultiplier : 1;

                Vector3 force = 15f * offset * acceleration * forceMultiplier.Value - rigidbody.velocity;

                if (force.magnitude > 15f)
                    force = force.normalized * 15f;

                rigidbody.AddForce(force, ForceMode.VelocityChange);

                GameCamera.m_instance.GetCameraPosition(dt, out _, out Quaternion rot);

                Quaternion quaternion = Quaternion.RotateTowards(__instance.transform.rotation, rot, __instance.m_turnRate * 15f * dt * rotationMultiplier.Value);
                
                __instance.transform.rotation = quaternion;

                return false;
            }

            private static void Postfix(Valkyrie __instance)
            {
                if (!modEnabled.Value)
                    return;

                if (__instance != controlledValkyrie)
                    return;

                Vector3 pos = __instance.transform.position;
                if (ZoneSystem.instance.GetGroundHeight(pos, out float height2))
                {
                    pos.y = Mathf.Min(Mathf.Max(pos.y, Mathf.Max(height2, ZoneSystem.instance.m_waterLevel) + 5f), maxAltitude.Value);
                    __instance.transform.position = pos;
                }
            }
        }
        
        [HarmonyPatch(typeof(Valkyrie), nameof(Valkyrie.DropPlayer))]
        public static class Valkyrie_DropPlayer_Dismount
        {
            private static void Postfix(Valkyrie __instance)
            {
                if (!modEnabled.Value)
                    return;

                if (__instance != controlledValkyrie)
                    return;

                if (isFlyingMountValkyrie)
                {
                    isFlyingMountValkyrie = false;

                    playerDropped = true;
                    if (!Player.m_localPlayer.GetSEMan().HaveStatusEffect(slowFallHash))
                    {
                        castSlowFall = true;
                        Player.m_localPlayer.GetSEMan().AddStatusEffect(slowFallHash);
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

        [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
        public static class Player_TeleportTo_TeleportDrop
        {
            private static void Prefix()
            {
                if ((bool)controlledValkyrie)
                {
                    controlledValkyrie.DropPlayer(destroy: true);
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
                    if (__instance.GetSEMan().HaveStatusEffect(slowFallHash))
                        __instance.GetSEMan().RemoveStatusEffect(slowFallHash, true);
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
        public static class Hud_UpdateCrosshair_ShowBowCrosshair
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(Hud __instance, Player player)
            {
                if (!modEnabled.Value) return;

                if (!isFlyingMountValkyrie)
                    return;

                if (crosshairState)
                {
                    bool showBowCrosshair = player.GetLeftItem() != null && player.GetLeftItem().m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow;
                    if (__instance.m_crosshair.enabled != showBowCrosshair)
                        __instance.m_crosshair.enabled = showBowCrosshair;
                }
            }
        }

        [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.GetEnvironmentOverride))]
        public static class EnvMan_GetEnvironmentOverride_EnableWeatherOnFlight
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(ref Player __state)
            {
                if (!modEnabled.Value || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;

                if (!isFlyingMountValkyrie)
                    return;

                __state = Player.m_localPlayer;

                Player.m_localPlayer = null;
            }

            [HarmonyPriority(Priority.First)]
            private static void Postfix(Player __state)
            {
                if (!modEnabled.Value || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;

                if (!isFlyingMountValkyrie)
                    return;

                Player.m_localPlayer = __state;
            }
        }

    }
}