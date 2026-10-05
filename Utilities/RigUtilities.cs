/*
 * Ratalyth Menu  Utilities/RigUtilities.cs
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

using GorillaNetworking;
using Photon.Pun;
using Photon.Realtime;
using PlayFab;
using PlayFab.ClientModels;
using Ratalyth.Extensions;
using Ratalyth.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Ratalyth.Utilities
{
    public class RigUtilities
    {
        public static VRRig GetVRRigFromPlayer(NetPlayer p) =>
            GorillaGameManager.StaticFindRigForPlayer(p);

        public static NetPlayer GetPlayerFromVRRig(VRRig p) =>
            p.Creator ?? NetworkSystem.Instance.GetPlayer(NetworkSystem.Instance.GetOwningPlayerID(p.rigSerializer.gameObject));

        public static NetPlayer GetPlayerFromID(string id) =>
            PhotonNetwork.PlayerList.FirstOrDefault(player => player.UserId == id);

        public static Player NetPlayerToPlayer(NetPlayer p) =>
            p.GetPlayerRef();

        public static Player GetRandomPlayer(bool includeSelf = false) =>
            includeSelf ?
            PhotonNetwork.PlayerList[Random.Range(0, PhotonNetwork.PlayerList.Length)] :
            PhotonNetwork.PlayerListOthers[Random.Range(0, PhotonNetwork.PlayerListOthers.Length)];

        private static VRRig rigTarget;
        private static float rigTargetChange;
        public static VRRig GetTargetPlayer(float targetChangeDelay = 1f)
        {
            if (!(Time.time > rigTargetChange) && rigTarget.Active()) return rigTarget;
            rigTargetChange = Time.time + targetChangeDelay;
            rigTarget = GetRandomVRRig();

            return rigTarget;
        }

        public static VRRig GetRandomVRRig(bool includeSelf = false) =>
            GetVRRigFromPlayer(GetRandomPlayer(includeSelf));

        public static NetworkView GetNetworkViewFromVRRig(VRRig p) =>
            p.netView;

        public static PhotonView GetPhotonViewFromVRRig(VRRig p) =>
            GetNetworkViewFromVRRig(p).GetView;

        public static VRRig GetClosestVRRig() =>
VRRig.LocalRig.GetClosest();

        public static readonly Dictionary<string, float> waitingForCreationDate = new Dictionary<string, float>();
        public static readonly Dictionary<string, string> creationDateCache = new Dictionary<string, string>();

        /// <summary>Returned while a lookup is still in flight, so it can be told apart from a failure.</summary>
        public const string CreationDateLoading = "Loading...";

        /// <summary>
        /// Returned once PlayFab has definitively refused. This is deliberately not the same string
        /// as a failure used to be, and is never written until the retries are actually spent.
        /// </summary>
        public const string CreationDateUnavailable = "Unavailable";

        private const float RetryDelay = 10f;
        private const int MaxAttempts = 3;

        private static readonly Dictionary<string, int> creationDateAttempts = new Dictionary<string, int>();
        private static readonly Dictionary<string, List<Action<string>>> pendingCreationDateCallbacks = new Dictionary<string, List<Action<string>>>();

        /// <summary>
        /// Resolves an account creation date, or explains why it cannot.
        ///
        /// Two things used to make this report "Error" for everybody. The PlayFab request was built
        /// with <c>PlayFabId = userId</c>, but every caller in this menu passes a Photon user id,
        /// which is not the identifier PlayFab accepts, so the call was rejected and the error
        /// callback wrote the string "Error" into the very same cache real dates go in. That entry
        /// was never evicted, so a single rejected request pinned every later lookup for that player
        /// to "Error" for the rest of the session no matter how many times it was retried. On top of
        /// that the error callback took no argument, which discarded the PlayFab error code and
        /// message, so the logs could never say why it was failing.
        ///
        /// Failures are now retried, only a definitive refusal is cached, and the PlayFab error is
        /// logged instead of discarded.
        /// </summary>
        public static string GetCreationDate(string input, Action<string> onTranslated = null, string format = "MMMM dd, yyyy h:mm tt")
        {
            if (string.IsNullOrEmpty(input))
                return CreationDateUnavailable;

            if (creationDateCache.TryGetValue(input, out string cached))
            {
                // A second caller for an id that has already resolved still expects to be told.
                onTranslated?.Invoke(cached);
                return cached;
            }

            if (onTranslated != null)
            {
                if (!pendingCreationDateCallbacks.TryGetValue(input, out List<Action<string>> callbacks))
                    pendingCreationDateCallbacks[input] = callbacks = new List<Action<string>>();

                callbacks.Add(onTranslated);
            }

            creationDateAttempts.TryGetValue(input, out int attemptCount);

            if (attemptCount < MaxAttempts)
            {
                waitingForCreationDate.TryGetValue(input, out float nextAttempt);

                if (Time.time >= nextAttempt)
                {
                    waitingForCreationDate[input] = Time.time + RetryDelay;
                    GetCreationCoroutine(input, format);
                }
            }

            return CreationDateLoading;
        }

        public static void GetCreationCoroutine(string userId, string format = "MMMM dd, yyyy h:mm tt")
        {
            if (creationDateCache.TryGetValue(userId, out string cached))
            {
                CompleteCreationDate(userId, cached);
                return;
            }

            string playFabId = ResolvePlayFabId(userId);

            if (string.IsNullOrEmpty(playFabId))
            {
                FailCreationDate(userId, "no PlayFab id could be resolved");
                return;
            }

            creationDateAttempts.TryGetValue(userId, out int attempt);
            creationDateAttempts[userId] = attempt + 1;

            // GetPlayerProfile is the client API meant for looking up an arbitrary player, and it
            // only returns the creation date when ShowCreated is explicitly requested.
            PlayFabClientAPI.GetPlayerProfile(
                new GetPlayerProfileRequest
                {
                    PlayFabId = playFabId,
                    ProfileConstraints = new PlayerProfileViewConstraints { ShowCreated = true }
                },
                result =>
                {
                    DateTime? created = result?.PlayerProfile?.Created;

                    if (created.HasValue && created.Value != default)
                    {
                        CompleteCreationDate(userId, created.Value.ToString(format));
                        return;
                    }

                    GetAccountInfoFallback(playFabId, userId, format);
                },
                error =>
                {
                    LogManager.LogWarning($"Creation date: GetPlayerProfile was refused for {playFabId}: {DescribePlayFabError(error)}");
                    GetAccountInfoFallback(playFabId, userId, format);
                });
        }

        /// <summary>
        /// GetAccountInfo is the older route to the same value. Kept as a fallback because titles
        /// that predate GetPlayerProfile exposing Created only answer here.
        /// </summary>
        private static void GetAccountInfoFallback(string playFabId, string userId, string format)
        {
            PlayFabClientAPI.GetAccountInfo(
                new GetAccountInfoRequest { PlayFabId = playFabId },
                result =>
                {
                    DateTime created = result?.AccountInfo?.Created ?? default;

                    if (created == default)
                    {
                        FailCreationDate(userId, $"PlayFab returned no creation date for {playFabId}");
                        return;
                    }

                    CompleteCreationDate(userId, created.ToString(format));
                },
                error => FailCreationDate(userId, DescribePlayFabError(error)));
        }

        /// <summary>
        /// PlayFab and Photon issue different identifiers and only the PlayFab one is accepted by the
        /// account APIs. The game exposes the PlayFab id solely for the local player, so that is
        /// resolved exactly when the caller is asking about themselves. For everyone else the Photon
        /// id is the only handle available and is passed through unchanged, which is how the rest of
        /// this menu already identifies players.
        /// </summary>
        private static string ResolvePlayFabId(string userId)
        {
            try
            {
                Player localPlayer = PhotonNetwork.LocalPlayer;
                PlayFabAuthenticator authenticator = PlayFabAuthenticator.instance;

                if (localPlayer != null && authenticator != null &&
                    string.Equals(localPlayer.UserId, userId, StringComparison.Ordinal))
                {
                    string localPlayFabId = authenticator.GetPlayFabPlayerId();

                    if (!string.IsNullOrEmpty(localPlayFabId))
                        return localPlayFabId;
                }
            }
            catch (Exception exception)
            {
                LogManager.LogWarning($"Creation date: could not read the local PlayFab id: {exception.Message}");
            }

            return userId;
        }

        private static string DescribePlayFabError(PlayFabError error) =>
            error == null ? "no error detail supplied" : $"{error.Error}: {error.ErrorMessage} (HTTP {error.HttpCode})";

        private static void CompleteCreationDate(string userId, string date)
        {
            creationDateCache[userId] = date;
            creationDateAttempts.Remove(userId);
            waitingForCreationDate.Remove(userId);

            DrainCreationDateCallbacks(userId, date);
        }

        /// <summary>
        /// A refusal is not cached here. The original code stored its failure in the date cache
        /// immediately and never cleared it, so one rejected request turned into a permanent "Error"
        /// for that player. Only once the retries are spent does this become a real answer.
        /// </summary>
        private static void FailCreationDate(string userId, string reason)
        {
            creationDateAttempts.TryGetValue(userId, out int attempt);
            waitingForCreationDate.Remove(userId);

            if (attempt < MaxAttempts)
                return;

            LogManager.LogError($"Creation date unavailable for {userId}: {reason}");
            CompleteCreationDate(userId, CreationDateUnavailable);
        }

        private static void DrainCreationDateCallbacks(string userId, string value)
        {
            if (!pendingCreationDateCallbacks.TryGetValue(userId, out List<Action<string>> callbacks))
                return;

            pendingCreationDateCallbacks.Remove(userId);

            foreach (Action<string> callback in callbacks)
            {
                try
                {
                    callback(value);
                }
                catch (Exception exception)
                {
                    LogManager.LogError($"Creation date callback failed for {userId}: {exception.Message}");
                }
            }
        }
    }
}