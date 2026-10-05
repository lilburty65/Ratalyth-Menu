/*
 * Ratalyth Menu  Extensions/VRRigExtensions.cs
 * A community driven mod menu for Gorilla Tag with over 1000+ mods
 *
 * Copyright (C) 2026  Seralyth Software
 * Copyright (C) 2026  Ratalyth
 * https://github.com/Ratalyth/Ratalyth-Menu
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using GorillaGameModes;
using GorillaTagScripts;
using Photon.Pun;
using Ratalyth.Menu;
using Ratalyth.Mods;
using Ratalyth.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Ratalyth.Menu.Main;
using static Ratalyth.Utilities.GameModeUtilities;

namespace Ratalyth.Extensions
{
    public static class VRRigExtensions
    {
        public static bool IsLocal(this VRRig rig, bool ghostRig = true) =>
            rig != null && (rig.isLocal || (GhostRig != null && rig == GhostRig));

        public static bool IsLeftHandGrabbable(this VRRig rig) =>
            rig != null && rig.leftHandLink.CanBeGrabbed();

        public static bool IsRightHandGrabbable(this VRRig rig) =>
           rig != null && rig.rightHandLink.CanBeGrabbed();

        public static bool IsHoldingLeftIndex(this VRRig rig) =>
            rig != null && rig.leftIndex.calcT > 0.8f;

        public static bool IsHoldingRightIndex(this VRRig rig) =>
              rig != null && rig.rightIndex.calcT > 0.8f;

        public static bool IsHoldingLeftGrip(this VRRig rig) =>
            rig != null && rig.leftMiddle.calcT > 0.8f;

        public static bool IsHoldingRightGrip(this VRRig rig) =>
           rig != null && rig.rightMiddle.calcT > 0.8f;

        public static float GetRecorderLoudness(this VRRig rig)
        {
            GorillaSpeakerLoudness recorder = rig.GetComponent<GorillaSpeakerLoudness>();
            if (recorder != null)
                return recorder.Loudness * 3f;
            return 0f;
        }
        public static bool IsBeingTouched(this VRRig rig, VRRig otherRig = null, float distance = 0.35f)
        {
            if (rig == null) return false;
            VRRig targetRig = otherRig ?? VRRig.LocalRig;
            return rig.Distance(targetRig.leftHand.rigTarget.position) <= distance || rig.Distance(targetRig.rightHand.rigTarget.position) <= distance;
        }

        public static bool IsNear(this VRRig rig, VRRig otherRig = null, float distance = 3f)
        {
            if (rig == null) return false;
            VRRig targetRig = otherRig ?? VRRig.LocalRig;
            return rig.Distance(targetRig.transform.position) <= distance;
        }

        public static bool IsTouchingMe(this VRRig rig, float distance = 0.35f)
        {
            if (rig == null) return false;
            return rig.Distance(VRRig.LocalRig.leftHand.rigTarget.position) <= distance || rig.Distance(VRRig.LocalRig.rightHand.rigTarget.position) <= distance;
        }

        public static bool IsTagged(this VRRig rig)
        {
            if (rig == null) return false;
            List<NetPlayer> infectedPlayers = InfectedList();
            NetPlayer targetPlayer = rig.GetPlayer();

            return infectedPlayers.Contains(targetPlayer);
        }

        public static bool IsSteam(this VRRig rig) =>
            rig.GetPlatform() != "Standalone";

        public static bool IsKIDRestricted(this VRRig rig) =>
!rig.IsMicEnabled && rig.GetName().ToLower().StartsWith("gorilla");

        /// <summary>
        /// Reads the platform the game actually reports for a player, instead of inferring it.
        ///
        /// This used to score cosmetic names, and every one of those signals had rotted. A rig's
        /// cosmetic set holds ids rather than display names, so "S. FIRST LOGIN" never matched
        /// anything and the Steam branch became unreachable, which is why the detector went silent
        /// instead of reporting Steam players. The scoring was self defeating even when the name
        /// did resolve, because "S. FIRST LOGIN" contains "FIRST LOGIN" and so scored a point for
        /// both Steam and PC, tying the counters and falling through to the default. Counting
        /// Photon custom properties was never a platform signal at all, it was a magic number that
        /// any patch adding or removing one property would flip for every player.
        ///
        /// The game already broadcasts each player's platform and NetworkSystem.GetPlayerPlatform
        /// is the supported reader for it, so that is asked first. Ranked sub tiers are tracked per
        /// platform, so a filled in tier is positive evidence for when the property has not landed.
        ///
        /// The returned vocabulary stays "Steam", "PC" and "Standalone" because callers feed this
        /// straight into nametag sprite lookups and the platform info panel.
        /// </summary>
        public static string GetPlatform(this VRRig rig)
        {
            if (rig == null)
                return "Standalone";

            string platform = null;

            try
            {
                NetPlayer player = rig.GetPlayer();
                NetworkSystem networkSystem = NetworkSystem.Instance;
                if (player != null && networkSystem != null)
                    platform = networkSystem.GetPlayerPlatform(player);
            }
            catch { }

            if (!string.IsNullOrEmpty(platform))
            {
                // The game's own values look like STEAM, QUEST, OCULUS_PC, PSVR and PICO, so match
                // on content rather than on an exact spelling that a future patch can change.
                if (platform.IndexOf("STEAM", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Steam";

                if (platform.IndexOf("QUEST", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    platform.IndexOf("META", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Standalone";

                // An explicit unknown is not evidence, so fall through to the ranked tiers instead
                // of treating it as a PC platform.
                if (platform.IndexOf("UNKNOWN", StringComparison.OrdinalIgnoreCase) < 0)
                    return "PC";
            }

            if (rig.currentRankedSubTierQuest > 0)
                return "Standalone";

            if (rig.currentRankedSubTierPC > 0)
                return "Steam";

            return "Standalone";
        }

        public static string GetCreationDate(this VRRig rig, Action<string> onTranslated = null, string format = "MMMM dd, yyyy h:mm tt") =>
            RigUtilities.GetCreationDate(rig.Creator.UserId, onTranslated, format);

        public static Color GetColor(this VRRig rig)
        {
            if (Buttons.GetIndex("Follow Player Colors").enabled)
                return rig.playerColor;

            if (rig.bodyRenderer.cosmeticBodyType == GorillaBodyType.Skeleton)
                return Color.green;

            switch (rig.setMatIndex)
            {
                case 1:
                    return Color.red;
                case 2:
                case 11:
                    return new Color32(255, 128, 0, 255);
                case 3:
                case 7:
                    return Color.blue;
                case 12:
                    return Color.green;
                default:
                    return rig.playerColor;
            }
        }

        public static bool Active(this VRRig rig) =>
            rig != null && ActiveRigs.Contains(rig);

        public static float Distance(this VRRig rig, Vector3 position) =>
            Vector3.Distance(rig.transform.position, position);

        public static float Distance(this VRRig rig, VRRig otherRig) =>
            rig.Distance(otherRig.transform.position);

        public static float Distance(this VRRig rig) =>
            rig.Distance(GorillaTagger.Instance.bodyCollider.transform.position);

        public static VRRig GetClosest(this VRRig rig) =>
            ActiveRigs.Where(targetRig => targetRig != null && targetRig != rig)
                                         .OrderBy(rig.Distance)
                                         .FirstOrDefault();

        public static int GetPing(this VRRig rig)
        {
            return playerPing.TryGetValue(rig, out int ping) ? ping : PhotonNetwork.GetPing();
        }

        public static int GetTruePing(this VRRig rig)
        {
            double ping = Math.Abs((rig.velocityHistoryList[0].time - PhotonNetwork.Time) * 1000);
            int safePing = (int)Math.Clamp(Math.Round(ping), 0, int.MaxValue);

            return safePing;
        }

        public static string GetName(this VRRig rig) =>
            RigUtilities.GetPlayerFromVRRig(rig)?.NickName ?? string.Empty;

        public static NetPlayer GetPlayer(this VRRig rig) =>
           RigUtilities.GetPlayerFromVRRig(rig);

        public static Photon.Realtime.Player GetPhotonPlayer(this VRRig rig) =>
            RigUtilities.NetPlayerToPlayer(RigUtilities.GetPlayerFromVRRig(rig));

        public static NetworkView GetNetView(this VRRig rig) =>
            rig.netView;

        public static PhotonView GetPhotonView(this VRRig rig) =>
            rig.netView.GetView;

        public static ProjectileWeapon GetSlingshot(this VRRig rig) =>
            rig.projectileWeapon;

        public static float[] GetSpeed(this VRRig rig)
        {
            NetPlayer player = rig.GetPlayer();
            switch (GorillaGameManager.instance.GameType())
            {
                case GameModeType.Infection:
                case GameModeType.InfectionCompetitive:
                case GameModeType.FreezeTag:
                case GameModeType.PropHunt:
                    GorillaTagManager tagManager = (GorillaTagManager)GorillaGameManager.instance;
                    return tagManager.isCurrentlyTag
                        ? player == tagManager.currentIt
                            ? (new[]
                            {
                                tagManager.fastJumpLimit,
                                tagManager.fastJumpMultiplier
                            })
                            : (new[]
                        {
                            tagManager.slowJumpLimit,
                            tagManager.slowJumpMultiplier
                        })
                        : tagManager.currentInfected.Contains(player)
                            ? (new[]
                            {
                                tagManager.InterpolatedInfectedJumpSpeed(tagManager.currentInfected.Count),
                                tagManager.InterpolatedInfectedJumpMultiplier(tagManager.currentInfected.Count)
                            })
                            : (new[]
                        {
                            tagManager.InterpolatedNoobJumpSpeed(tagManager.currentInfected.Count),
                            tagManager.InterpolatedNoobJumpMultiplier(tagManager.currentInfected.Count)
                        });
                default:
                    return new[] { 6.5f, 1.1f };
            }
        }

        public static float GetMaxSpeed(this VRRig rig) =>
            rig.GetSpeed()[0];

        public static float GetSpeedMultiplier(this VRRig rig) =>
            rig.GetSpeed()[1];

        public static string Cosmetics(this VRRig rig) =>
            rig._playerOwnedCosmetics.Concat();

        public static bool IsVIMSubscriber(this VRRig rig) =>
            SubscriptionManager.Instance.subData[rig.GetPlayer()].active;


        private static readonly List<VRRig> _rigs = new List<VRRig>();
        private static int _lastFrame = -1;
        private static readonly object _lock = new object(); // just in case we aren't on unity's thread
        public static List<VRRig> ActiveRigs
        {
            get
            {
                int frame = Time.frameCount;
                if (frame == _lastFrame)
                    return _rigs;

                lock (_lock)
                {
                    if (frame == _lastFrame)
                        return _rigs;

                    _lastFrame = frame;
                    _rigs.Clear();

                    foreach (var rig in VRRigCache.ActiveRigs)
                    {
                        if (rig != null && !Settings.Blocked.Contains(rig))
                            _rigs.Add(rig);
                    }
                }

                return _rigs;
            }
        }
    }
}
