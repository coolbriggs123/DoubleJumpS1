using System.Text;
using Il2CppSteamworks;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(DoubleJump.Core), "DoubleJump", "1.2.0", "Coolbriggs", null)]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace DoubleJump
{
    public class ModConfig
    {
        public float JumpForce = 7f;
        public bool EnableDoubleJump = true;
        public float MaxJumpHeight = 10f;
        public bool AllowTripleJump = false;
        public bool SyncConfig = true;
    }

    public class Core : MelonMod
    {
        // Custom validator class since ValueRange isn't available
        private class FloatRangeValidator
        {
            private float min;
            private float max;

            public FloatRangeValidator(float min, float max)
            {
                this.min = min;
                this.max = max;
            }

            public float Validate(float value)
            {
                return Mathf.Clamp(value, min, max);
            }
        }

        private static ModConfig config = new ModConfig();
        private const string CATEGORY_GENERAL = "DoubleJump";
        private const string SETTING_JUMP_FORCE = "JumpForce";
        private const string SETTING_ENABLE_DOUBLE_JUMP = "EnableDoubleJump";
        private const string SETTING_MAX_JUMP_HEIGHT = "MaxJumpHeight";
        private const string SETTING_ALLOW_TRIPLE_JUMP = "AllowTripleJump";
        private const string SETTING_SYNC_CONFIG = "SyncConfig";

        // Host gameplay values (used for syncing)
        private static float hostJumpForce = 7f;
        private static bool hostDoubleJumpEnabled = true;
        private static float hostMaxJumpHeight = 10f;
        private static bool hostTripleJumpAllowed = false;

        // Active gameplay values (used for actual gameplay)
        private static float activeJumpForce = 7f;
        private static bool activeDoubleJumpEnabled = true;
        private static float activeMaxJumpHeight = 10f;
        private static bool activeTripleJumpAllowed = false;

        // Local config values (never overwritten by sync)
        private static float localJumpForce = 7f;
        private static bool localDoubleJumpEnabled = true;
        private static float localMaxJumpHeight = 10f;
        private static bool localTripleJumpAllowed = false;

        private static bool isInitialized = false;
        private static List<PlayerData> players = new List<PlayerData>();
        private static float playerSearchInterval = 5f;
        private static float lastPlayerSearchTime = 0f;
        private static CSteamID localSteamID;
        private static bool debugMode = false;
        private static bool isHost = false;
        private static bool configSynced = false;

        // Network message constants
        private const string CONFIG_MESSAGE_PREFIX = "DJMP_CONFIG:";
        private const int CONFIG_SYNC_INTERVAL = 10;
        private static float lastConfigSyncTime = 0f;

        // Class to store per-player data
        private class PlayerData
        {
            public GameObject playerObject;
            public Rigidbody playerRigidbody;
            public CharacterController playerController;
            public bool canDoubleJump = true;
            public bool hasDoubleJumped = false;
            public bool wasGroundedLastFrame = true;
            public bool hasJumpedOnce = false;
            public int jumpCount = 0;
            public float doubleJumpVelocity = 0f;
            public Vector3 playerVelocity = Vector3.zero;
            public bool isLocalPlayer = false;
            public CSteamID? steamID = null;
            public bool isRealPlayer = false;
            public bool isHost = false;

            public PlayerData(GameObject obj)
            {
                playerObject = obj;
                playerController = obj.GetComponent<CharacterController>();
                playerRigidbody = obj.GetComponent<Rigidbody>();

                if (playerRigidbody == null)
                {
                    playerRigidbody = obj.GetComponentInChildren<Rigidbody>(true);
                }

                TryGetSteamID();
                DetermineIfRealPlayer();
            }

            public void ResetJumpState()
            {
                canDoubleJump = true;
                hasDoubleJumped = false;
                hasJumpedOnce = false;
                jumpCount = 0;
            }

            private void TryGetSteamID()
            {
                try
                {
                    string name = playerObject.name;
                    if (name.Contains("(") && name.Contains(")"))
                    {
                        int startIndex = name.IndexOf("(") + 1;
                        int endIndex = name.IndexOf(")");
                        if (startIndex < endIndex)
                        {
                            string potentialID = name.Substring(startIndex, endIndex - startIndex);
                            if (ulong.TryParse(potentialID, out ulong steamIDValue))
                            {
                                steamID = new CSteamID(steamIDValue);
                                return;
                            }
                        }
                    }

                    var components = playerObject.GetComponents<Component>();
                    foreach (var component in components)
                    {
                        if (component == null) continue;

                        var type = component.GetType();
                        var fields = type.GetFields();
                        foreach (var field in fields)
                        {
                            if (field.FieldType == typeof(CSteamID) || field.Name.ToLower().Contains("steamid"))
                            {
                                var value = field.GetValue(component);
                                if (value != null && value is CSteamID id)
                                {
                                    steamID = id;
                                    return;
                                }
                            }
                        }

                        var properties = type.GetProperties();
                        foreach (var property in properties)
                        {
                            if (property.PropertyType == typeof(CSteamID) || property.Name.ToLower().Contains("steamid"))
                            {
                                var value = property.GetValue(component);
                                if (value != null && value is CSteamID id)
                                {
                                    steamID = id;
                                    return;
                                }
                            }
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    if (debugMode)
                        MelonLogger.Error($"Error getting Steam ID: {ex.Message}");
                }
            }

            private void DetermineIfRealPlayer()
            {
                if (steamID.HasValue && steamID.Value.m_SteamID != 0)
                {
                    isRealPlayer = true;
                    return;
                }

                if (playerController != null && playerController.enabled && playerObject.activeInHierarchy)
                {
                    isRealPlayer = true;
                    return;
                }

                string name = playerObject.name.ToLower();
                if ((name.Contains("player") || name.Contains("character") || name.Contains("avatar")) &&
                    !name.Contains("prefab") && !name.Contains("template") && !name.Contains("model"))
                {
                    isRealPlayer = true;
                }
            }
        }

        public override void OnInitializeMelon()
        {
            var category = MelonPreferences.CreateCategory(CATEGORY_GENERAL, "Double Jump Settings");

            // Using standard MelonPreferences entries without ValueRange
            category.CreateEntry(
                SETTING_JUMP_FORCE,
                config.JumpForce,
                "Jump Force",
                "How much force to apply for double jumps"
            );

            category.CreateEntry(
                SETTING_ENABLE_DOUBLE_JUMP,
                config.EnableDoubleJump,
                "Enable Double Jump",
                "Toggle double jump functionality"
            );

            category.CreateEntry(
                SETTING_MAX_JUMP_HEIGHT,
                config.MaxJumpHeight,
                "Max Jump Height",
                "Maximum height allowed for jumps"
            );

            category.CreateEntry(
                SETTING_ALLOW_TRIPLE_JUMP,
                config.AllowTripleJump,
                "Allow Triple Jump",
                "Enable triple jump capability"
            );

            category.CreateEntry(
                SETTING_SYNC_CONFIG,
                config.SyncConfig,
                "Sync Config (Host Only)",
                "When enabled, host's settings will be synced to all clients"
            );

            LoadConfig();
            UpdateLocalValues();
            UpdateHostValues();
            UpdateActiveValues();
        }

        private bool IsInMultiplayer()
        {
            try
            {
                if (!SteamAPI.Init()) return false;

                CSteamID lobbyId = SteamMatchmaking.GetLobbyByIndex(0);
                int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
                return memberCount > 1; // More than 1 player means multiplayer
            }
            catch
            {
                return false;
            }
        }

        private void LoadConfig()
        {
            // Add validation when loading values
            config.JumpForce = Mathf.Clamp(
                MelonPreferences.GetEntryValue<float>(CATEGORY_GENERAL, SETTING_JUMP_FORCE),
                1f, 20f
            );

            config.EnableDoubleJump = MelonPreferences.GetEntryValue<bool>(CATEGORY_GENERAL, SETTING_ENABLE_DOUBLE_JUMP);

            config.MaxJumpHeight = Mathf.Clamp(
                MelonPreferences.GetEntryValue<float>(CATEGORY_GENERAL, SETTING_MAX_JUMP_HEIGHT),
                1f, 30f
            );

            config.AllowTripleJump = MelonPreferences.GetEntryValue<bool>(CATEGORY_GENERAL, SETTING_ALLOW_TRIPLE_JUMP);
            config.SyncConfig = MelonPreferences.GetEntryValue<bool>(CATEGORY_GENERAL, SETTING_SYNC_CONFIG);
        }

        private void UpdateLocalValues()
        {
            localJumpForce = config.JumpForce;
            localDoubleJumpEnabled = config.EnableDoubleJump;
            localMaxJumpHeight = config.MaxJumpHeight;
            localTripleJumpAllowed = config.AllowTripleJump;
        }

        private void UpdateHostValues()
        {
            if (isHost)
            {
                hostJumpForce = config.JumpForce;
                hostDoubleJumpEnabled = config.EnableDoubleJump;
                hostMaxJumpHeight = config.MaxJumpHeight;
                hostTripleJumpAllowed = config.AllowTripleJump;

                if (config.SyncConfig && IsInMultiplayer())
                {
                    SyncConfigToClients();
                }
            }
        }

        private void UpdateActiveValues()
        {
            // In singleplayer, always use local values
            if (!IsInMultiplayer())
            {
                activeJumpForce = localJumpForce;
                activeDoubleJumpEnabled = localDoubleJumpEnabled;
                activeMaxJumpHeight = localMaxJumpHeight;
                activeTripleJumpAllowed = localTripleJumpAllowed;
                return;
            }

            // In multiplayer, respect sync settings
            if (isHost || !config.SyncConfig)
            {
                activeJumpForce = localJumpForce;
                activeDoubleJumpEnabled = localDoubleJumpEnabled;
                activeMaxJumpHeight = localMaxJumpHeight;
                activeTripleJumpAllowed = localTripleJumpAllowed;
            }
            else
            {
                activeJumpForce = hostJumpForce;
                activeDoubleJumpEnabled = hostDoubleJumpEnabled;
                activeMaxJumpHeight = hostMaxJumpHeight;
                activeTripleJumpAllowed = hostTripleJumpAllowed;
            }
        }



        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName != "Main") return;

            isInitialized = false;
            configSynced = false;
            players.Clear();
            MelonCoroutines.Start(DelayedInit());
        }

        private System.Collections.IEnumerator DelayedInit()
        {
            yield return new WaitForSeconds(1.0f); // Increased delay to ensure scene is fully loaded
            if (GameObject.Find("Player") != null) // Only proceed if we're in a valid scene with players

                FindAllPlayers();
            DetermineIfHost();
            isInitialized = true;
            try
            {
                localSteamID = SteamUser.GetSteamID();
            }
            catch (System.Exception)
            {
                // Silent catch
            }
        }



        public override void OnUpdate()
        {
            if (!isInitialized || !isHost) return;

            // Only sync config in Main scene
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Main") return;

            // Periodically search for new players
            if (Time.time - lastPlayerSearchTime > playerSearchInterval)
            {
                FindAllPlayers();
                lastPlayerSearchTime = Time.time;
            }

            // If we're the host and config syncing is enabled, periodically sync config
            if (config.SyncConfig && !configSynced && Time.time - lastConfigSyncTime > CONFIG_SYNC_INTERVAL)
            {
                SyncConfigToClients();
                lastConfigSyncTime = Time.time;
            }

            // Process each player
            foreach (PlayerData player in players)
            {
                if (player.isRealPlayer) // Only process real players
                {
                    ProcessPlayerJump(player);
                }
            }
        }

        private void ProcessPlayerJump(PlayerData player)
        {
            if (player.playerObject == null || !config.EnableDoubleJump)
                return;

            bool isGrounded = false;

            // Check if grounded using CharacterController if available
            if (player.playerController != null)
            {
                isGrounded = player.playerController.isGrounded;
            }
            else
            {
                // Fallback to raycast method
                isGrounded = IsGroundedRaycast(player);
            }

            // Only process input for the local player
            bool jumpKeyPressed = player.isLocalPlayer && Input.GetKeyDown(KeyCode.Space);

            // Reset abilities when landing
            if (isGrounded)
            {
                if (!player.wasGroundedLastFrame)
                {
                    player.ResetJumpState();
                }

                // Track initial jump from ground
                if (jumpKeyPressed)
                {
                    player.hasJumpedOnce = true;
                    player.jumpCount = 1;
                }
            }
            // Handle double/triple jump - when in air and after first jump
            else if (!isGrounded && jumpKeyPressed && player.hasJumpedOnce)
            {
                // Check if we can do another jump
                bool canJumpAgain = false;

                if (!player.hasDoubleJumped)
                {
                    // First air jump (double jump)
                    canJumpAgain = true;
                }
                else if (config.AllowTripleJump && player.jumpCount < 3)
                {
                    // Second air jump (triple jump)
                    canJumpAgain = true;
                }

                if (canJumpAgain)
                {
                    // Execute jump
                    player.jumpCount++;

                    if (player.playerController != null)
                    {
                        // Apply vertical velocity directly to the character controller
                        player.doubleJumpVelocity = config.JumpForce;
                    }
                    else if (player.playerRigidbody != null)
                    {
                        // Reset vertical velocity before applying force
                        player.playerRigidbody.velocity = new Vector3(
                            player.playerRigidbody.velocity.x,
                            0f,
                            player.playerRigidbody.velocity.z
                        );
                        player.playerRigidbody.AddForce(Vector3.up * config.JumpForce, ForceMode.Impulse);
                    }

                    player.hasDoubleJumped = true;

                    if (player.jumpCount >= 3 || !config.AllowTripleJump)
                    {
                        player.canDoubleJump = false;
                    }
                }
            }

            player.wasGroundedLastFrame = isGrounded;
        }

        public override void OnFixedUpdate()
        {
            if (!isInitialized)
                return;

            foreach (PlayerData player in players)
            {
                if (!player.isRealPlayer) continue;

                // Apply double jump velocity to character controller if needed
                if (player.playerController != null && player.doubleJumpVelocity > 0)
                {
                    player.playerVelocity.y = player.doubleJumpVelocity;
                    player.doubleJumpVelocity = 0f;

                    // Apply gravity in subsequent frames
                    MelonCoroutines.Start(ApplyGravity(player));
                }
            }
        }

        private System.Collections.IEnumerator ApplyGravity(PlayerData player)
        {
            // Wait a bit to let the jump happen
            yield return new WaitForSeconds(0.1f);

            // Then start applying gravity
            while (player.playerController != null && !player.playerController.isGrounded)
            {
                player.playerVelocity.y += Physics.gravity.y * Time.deltaTime;

                // Limit the maximum jump height
                if (player.playerVelocity.y > 0 && player.playerObject.transform.position.y > config.MaxJumpHeight)
                {
                    player.playerVelocity.y = 0;
                }

                player.playerController.Move(player.playerVelocity * Time.deltaTime);
                yield return null;
            }

            // Reset velocity when grounded
            player.playerVelocity = Vector3.zero;
        }

        private void FindAllPlayers()
        {
            try
            {
                // Keep track of existing players to avoid duplicates
                HashSet<GameObject> existingPlayerObjects = new HashSet<GameObject>();
                foreach (PlayerData player in players)
                {
                    if (player.playerObject != null)
                    {
                        existingPlayerObjects.Add(player.playerObject);
                    }
                }

                // Method 1: Find all CharacterControllers (most reliable)
                CharacterController[] controllers = GameObject.FindObjectsOfType<CharacterController>();
                foreach (CharacterController controller in controllers)
                {
                    if (controller != null && !existingPlayerObjects.Contains(controller.gameObject))
                    {
                        // Skip objects that are clearly not players
                        string name = controller.gameObject.name.ToLower();
                        if (name.Contains("npc") || name.Contains("enemy") || name.Contains("dummy") ||
                            name.Contains("target") || name.Contains("screen") || name.Contains("prefab"))
                        {
                            continue;
                        }

                        AddPlayer(controller.gameObject);
                        existingPlayerObjects.Add(controller.gameObject);
                    }
                }

                // Method 2: Find all objects with "Player" tag
                try
                {
                    GameObject[] taggedPlayers = GameObject.FindGameObjectsWithTag("Player");
                    foreach (GameObject playerObj in taggedPlayers)
                    {
                        if (playerObj != null && !existingPlayerObjects.Contains(playerObj))
                        {
                            AddPlayer(playerObj);
                            existingPlayerObjects.Add(playerObj);
                        }
                    }
                }
                catch (System.Exception)
                {
                    // Tag might not exist, ignore
                }

                // Method 3: Find objects with specific player names (more selective)
                string[] specificPlayerNames = { "Player", "LocalPlayer", "NetworkPlayer" };
                foreach (string name in specificPlayerNames)
                {
                    // Try to find objects by exact name
                    GameObject obj = GameObject.Find(name);
                    if (obj != null && !existingPlayerObjects.Contains(obj))
                    {
                        AddPlayer(obj);
                        existingPlayerObjects.Add(obj);
                    }
                }

                // Clean up any null references and non-real players
                players.RemoveAll(p => p.playerObject == null || !p.isRealPlayer);

                // Try to identify the local player
                IdentifyLocalPlayer();
            }
            catch (System.Exception)
            {
                // Silent catch
            }
        }

        private void AddPlayer(GameObject playerObj)
        {
            try
            {
                PlayerData newPlayer = new PlayerData(playerObj);

                // Skip if not a real player
                if (!newPlayer.isRealPlayer)
                {
                    return;
                }

                // If we have neither controller nor rigidbody, add a rigidbody
                if (newPlayer.playerRigidbody == null && newPlayer.playerController == null)
                {
                    newPlayer.playerRigidbody = playerObj.AddComponent<Rigidbody>();
                    newPlayer.playerRigidbody.freezeRotation = true;
                    newPlayer.playerRigidbody.constraints = RigidbodyConstraints.FreezeRotation;
                }

                players.Add(newPlayer);
            }
            catch (System.Exception)
            {
                // Silent catch
            }
        }

        private void IdentifyLocalPlayer()
        {
            try
            {
                // Reset all players to non-local first
                foreach (PlayerData player in players)
                {
                    player.isLocalPlayer = false;
                }

                // Method 1: Use Steam ID to identify local player
                if (localSteamID.m_SteamID != 0)
                {
                    foreach (PlayerData player in players)
                    {
                        if (player.steamID.HasValue && player.steamID.Value.m_SteamID == localSteamID.m_SteamID)
                        {
                            player.isLocalPlayer = true;
                            return;
                        }
                    }
                }

                // Method 2: Try to find the main camera
                Camera mainCamera = Camera.main;
                if (mainCamera != null)
                {
                    // The player that the main camera is attached to or following is likely the local player
                    Transform cameraTransform = mainCamera.transform;

                    // Check if camera is directly attached to a player
                    foreach (PlayerData player in players)
                    {
                        if (cameraTransform.IsChildOf(player.playerObject.transform) ||
                            cameraTransform.parent == player.playerObject.transform)
                        {
                            player.isLocalPlayer = true;
                            return;
                        }
                    }

                    // If not directly attached, find the closest player to the camera
                    float closestDistance = float.MaxValue;
                    PlayerData closestPlayer = null;

                    foreach (PlayerData player in players)
                    {
                        float distance = Vector3.Distance(cameraTransform.position, player.playerObject.transform.position);
                        if (distance < closestDistance)
                        {
                            closestDistance = distance;
                            closestPlayer = player;
                        }
                    }

                    if (closestPlayer != null && closestDistance < 5f) // Assume within 5 units is the followed player
                    {
                        closestPlayer.isLocalPlayer = true;
                        return;
                    }
                }

                // Fallback: if we only have one player, assume it's local
                if (players.Count == 1)
                {
                    players[0].isLocalPlayer = true;
                    return;
                }

                // Another fallback: look for common local player naming patterns
                foreach (PlayerData player in players)
                {
                    string name = player.playerObject.name.ToLower();
                    if (name.Contains("local") || name.Contains("my") || name.Contains("self") ||
                        name.Contains("own") || name == "player")
                    {
                        player.isLocalPlayer = true;
                        return;
                    }
                }
            }
            catch (System.Exception)
            {
                // Silent catch
            }
        }

        private void DetermineIfHost()
        {
            try
            {
                if (!IsInMultiplayer())
                {
                    isHost = true;
                    configSynced = true; // No need to sync in singleplayer
                    LoggerInstance.Msg("Singleplayer mode detected");
                    return;
                }

                localSteamID = SteamUser.GetSteamID();
                CSteamID lobbyOwner = SteamMatchmaking.GetLobbyOwner(SteamMatchmaking.GetLobbyByIndex(0));
                isHost = lobbyOwner.m_SteamID == localSteamID.m_SteamID;
                LoggerInstance.Msg($"Multiplayer mode - You are {(isHost ? "host" : "client")}");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Failed to determine host status: {ex.Message}");
                isHost = true; // Default to true in case of error
                configSynced = true;
            }
        }

        private static bool IsGroundedRaycast(PlayerData player)
        {
            if (player.playerObject == null) return true;

            Vector3 rayStart = player.playerObject.transform.position + Vector3.up * 0.1f;
            float rayLength = 0.3f;
            return Physics.Raycast(rayStart, Vector3.down, rayLength);
        }

        // Config synchronization methods
        private void SyncConfigToClients()
        {
            if (!isHost || !config.SyncConfig || !IsInMultiplayer()) return;

            try
            {
                string configData = $"{CONFIG_MESSAGE_PREFIX}{hostJumpForce}|{hostDoubleJumpEnabled}|{hostMaxJumpHeight}|{hostTripleJumpAllowed}";

                CSteamID lobbyId = SteamMatchmaking.GetLobbyByIndex(0);
                int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyId);

                for (int i = 0; i < memberCount; i++)
                {
                    CSteamID memberId = SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i);
                    if (memberId.m_SteamID != localSteamID.m_SteamID)
                    {
                        byte[] data = Encoding.UTF8.GetBytes(configData);
                        SteamNetworking.SendP2PPacket(memberId, data, (uint)data.Length, EP2PSend.k_EP2PSendReliable);
                    }
                }

                configSynced = true;
                LoggerInstance.Msg("Gameplay values synced to all clients");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Error syncing config: {ex.Message}");
            }
        }

        // Steam callbacks for networking
        private static class SteamCallbacks
        {
            private static Callback<P2PSessionRequest_t> p2pSessionRequestCallback;
            private static Callback<P2PSessionConnectFail_t> p2pSessionConnectFailCallback;


            public static void RegisterCallbacks()
            {
                // Use the static Create method with an Action delegate
                p2pSessionRequestCallback = Callback<P2PSessionRequest_t>.Create(new Action<P2PSessionRequest_t>(OnP2PSessionRequest));
                p2pSessionConnectFailCallback = Callback<P2PSessionConnectFail_t>.Create(new Action<P2PSessionConnectFail_t>(OnP2PSessionConnectFail));

                // Start polling for messages
                MelonCoroutines.Start(PollForMessages());
            }

            private static void OnP2PSessionRequest(P2PSessionRequest_t param)
            {
                // Accept all session requests
                SteamNetworking.AcceptP2PSessionWithUser(param.m_steamIDRemote);
            }

            private static void OnP2PSessionConnectFail(P2PSessionConnectFail_t param)
            {
                if (debugMode)
                {
                    MelonLogger.Warning($"P2P connection failed: {param.m_eP2PSessionError}");
                }
            }

            private static System.Collections.IEnumerator PollForMessages()
            {
                while (true)
                {
                    yield return new WaitForSeconds(0.5f);

                    uint msgSize;
                    while (SteamNetworking.IsP2PPacketAvailable(out msgSize))
                    {
                        byte[] data = new byte[msgSize];
                        CSteamID senderId;

                        if (SteamNetworking.ReadP2PPacket(data, msgSize, out msgSize, out senderId))
                        {
                            string message = Encoding.UTF8.GetString(data);

                            // Check if it's a config message
                            if (message.StartsWith(CONFIG_MESSAGE_PREFIX))
                            {
                                MelonLogger.Msg($"Received config from {senderId.m_SteamID}");
                                // Use the static method to process the message
                                ProcessReceivedConfigMessage(message);
                            }
                        }
                    }
                }
            }

            // Static method to process config messages
            private static void ProcessReceivedConfigMessage(string message)
            {
                if (!message.StartsWith(CONFIG_MESSAGE_PREFIX)) return;

                try
                {
                    string configData = message.Substring(CONFIG_MESSAGE_PREFIX.Length);
                    string[] parts = configData.Split('|');

                    if (parts.Length >= 4)
                    {
                        // Create temporary variables for validation
                        float newJumpForce;
                        bool newEnableDoubleJump;
                        float newMaxJumpHeight;
                        bool newAllowTripleJump;

                        // Validate each value before applying
                        if (!float.TryParse(parts[0], out newJumpForce) ||
                            !bool.TryParse(parts[1], out newEnableDoubleJump) ||
                            !float.TryParse(parts[2], out newMaxJumpHeight) ||
                            !bool.TryParse(parts[3], out newAllowTripleJump))
                        {
                            MelonLogger.Error("Invalid config values received - keeping current config");
                            return;
                        }

                        // Additional sanity checks
                        if (newJumpForce <= 0 || newMaxJumpHeight <= 0)
                        {
                            MelonLogger.Error("Invalid jump values received - keeping current config");
                            return;
                        }

                        // If all validation passes, apply the new values to runtime config only
                        config.JumpForce = newJumpForce;
                        config.EnableDoubleJump = newEnableDoubleJump;
                        config.MaxJumpHeight = newMaxJumpHeight;
                        config.AllowTripleJump = newAllowTripleJump;

                        MelonLogger.Msg("Received and applied valid config from host");
                        configSynced = true;
                    }
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Error($"Failed to process config message: {ex.Message}");
                }
            }
        }
        public override void OnPreferencesSaved()
        {
            bool configChanged = false;

            // Add validation when saving values
            float newJumpForce = Mathf.Clamp(
                MelonPreferences.GetEntryValue<float>(CATEGORY_GENERAL, SETTING_JUMP_FORCE),
                1f, 20f
            );

            bool newDoubleJumpEnabled = MelonPreferences.GetEntryValue<bool>(CATEGORY_GENERAL, SETTING_ENABLE_DOUBLE_JUMP);

            float newMaxJumpHeight = Mathf.Clamp(
                MelonPreferences.GetEntryValue<float>(CATEGORY_GENERAL, SETTING_MAX_JUMP_HEIGHT),
                1f, 30f
            );

            bool newTripleJumpAllowed = MelonPreferences.GetEntryValue<bool>(CATEGORY_GENERAL, SETTING_ALLOW_TRIPLE_JUMP);
            bool newSyncConfig = MelonPreferences.GetEntryValue<bool>(CATEGORY_GENERAL, SETTING_SYNC_CONFIG);

            if (Math.Abs(newJumpForce - config.JumpForce) > 0.01f)
            {
                config.JumpForce = newJumpForce;
                configChanged = true;
            }

            if (newDoubleJumpEnabled != config.EnableDoubleJump)
            {
                config.EnableDoubleJump = newDoubleJumpEnabled;
                configChanged = true;
            }

            if (Math.Abs(newMaxJumpHeight - config.MaxJumpHeight) > 0.01f)
            {
                config.MaxJumpHeight = newMaxJumpHeight;
                configChanged = true;
            }

            if (newTripleJumpAllowed != config.AllowTripleJump)
            {
                config.AllowTripleJump = newTripleJumpAllowed;
                configChanged = true;
            }

            if (newSyncConfig != config.SyncConfig)
            {
                config.SyncConfig = newSyncConfig;
                configChanged = true;
            }

            if (configChanged)
            {
                UpdateLocalValues();
                UpdateHostValues();
                UpdateActiveValues();

                // Save to MelonPreferences
                MelonPreferences.Save();

                if (isHost && config.SyncConfig && IsInMultiplayer())
                {
                    SyncConfigToClients();
                }
            }
        }
    }
}
