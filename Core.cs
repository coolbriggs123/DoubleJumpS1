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

        // Simple JSON serialization
        public string ToJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"JumpForce\": {JumpForce.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
            sb.AppendLine($"  \"EnableDoubleJump\": {EnableDoubleJump.ToString().ToLower()},");
            sb.AppendLine($"  \"MaxJumpHeight\": {MaxJumpHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
            sb.AppendLine($"  \"AllowTripleJump\": {AllowTripleJump.ToString().ToLower()},");
            sb.AppendLine($"  \"SyncConfig\": {SyncConfig.ToString().ToLower()}");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // Simple JSON deserialization
        public static ModConfig FromJson(string json)
        {
            ModConfig config = new ModConfig();

            try
            {
                // Parse float values
                config.JumpForce = ParseFloatFromJson(json, "JumpForce", config.JumpForce);
                config.MaxJumpHeight = ParseFloatFromJson(json, "MaxJumpHeight", config.MaxJumpHeight);

                // Parse boolean values
                config.EnableDoubleJump = ParseBoolFromJson(json, "EnableDoubleJump", config.EnableDoubleJump);
                config.AllowTripleJump = ParseBoolFromJson(json, "AllowTripleJump", config.AllowTripleJump);
                config.SyncConfig = ParseBoolFromJson(json, "SyncConfig", config.SyncConfig);
            }
            catch (Exception)
            {
                // If parsing fails, return default config
            }

            return config;
        }

        private static float ParseFloatFromJson(string json, string key, float defaultValue)
        {
            string pattern = $"\"{key}\"\\s*:\\s*([0-9.]+)";
            var match = System.Text.RegularExpressions.Regex.Match(json, pattern);
            if (match.Success && match.Groups.Count > 1)
            {
                if (float.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float result))
                {
                    return result;
                }
            }
            return defaultValue;
        }

        private static bool ParseBoolFromJson(string json, string key, bool defaultValue)
        {
            string pattern = $"\"{key}\"\\s*:\\s*(true|false)";
            var match = System.Text.RegularExpressions.Regex.Match(json, pattern);
            if (match.Success && match.Groups.Count > 1)
            {
                if (bool.TryParse(match.Groups[1].Value, out bool result))
                {
                    return result;
                }
            }
            return defaultValue;
        }
    }

    public class Core : MelonMod
    {
        private static ModConfig config = new ModConfig();

        // Host gameplay values
        private static float hostJumpForce = 7f;
        private static bool hostDoubleJumpEnabled = true;
        private static float hostMaxJumpHeight = 10f;
        private static bool hostTripleJumpAllowed = false;

        // Client gameplay values
        private static float clientJumpForce = 7f;
        private static bool clientDoubleJumpEnabled = true;
        private static float clientMaxJumpHeight = 10f;
        private static bool clientTripleJumpAllowed = false;

        private static bool isInitialized = false;
        private static List<PlayerData> players = new List<PlayerData>();
        private static float playerSearchInterval = 5f; // Increased to reduce overhead
        private static float lastPlayerSearchTime = 0f;
        private static CSteamID localSteamID;
        private static bool debugMode = false; // Set to true for verbose logging
        private static bool isHost = false;
        private static bool configSynced = false;

        // Network message constants
        private const string CONFIG_MESSAGE_PREFIX = "DJMP_CONFIG:";
        private const int CONFIG_SYNC_INTERVAL = 10; // Seconds between config sync attempts
        private static float lastConfigSyncTime = 0f;

        // Config file paths
        private static string CONFIG_DIRECTORY = Path.Combine("UserData", "DoubleJump");
        private static string CONFIG_FILE = Path.Combine(CONFIG_DIRECTORY, "config.json");

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
            public bool isRealPlayer = false; // Flag to indicate if this is likely a real player
            public bool isHost = false;

            public PlayerData(GameObject obj)
            {
                playerObject = obj;
                playerController = obj.GetComponent<CharacterController>();
                playerRigidbody = obj.GetComponent<Rigidbody>();

                // If no rigidbody found, try to find in children
                if (playerRigidbody == null)
                {
                    playerRigidbody = obj.GetComponentInChildren<Rigidbody>(true);
                }

                // Try to find Steam ID component or property
                TryGetSteamID();

                // Determine if this is likely a real player
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
                    // Try different methods to get Steam ID

                    // Method 1: Check if the object name contains a Steam ID (common format)
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

                    // Method 2: Check for a component that might have SteamID
                    var components = playerObject.GetComponents<Component>();
                    foreach (var component in components)
                    {
                        if (component == null) continue;

                        // Look for properties or fields that might contain SteamID
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

                        // Check properties too
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
                // A real player is likely to have:
                // 1. A CharacterController or Rigidbody
                // 2. A Steam ID
                // 3. A name that suggests it's a player
                // 4. Active components that suggest player control

                // Check if it has a Steam ID
                if (steamID.HasValue && steamID.Value.m_SteamID != 0)
                {
                    isRealPlayer = true;
                    return;
                }

                // Check if it has a CharacterController and is active
                if (playerController != null && playerController.enabled && playerObject.activeInHierarchy)
                {
                    isRealPlayer = true;
                    return;
                }

                // Check for player-like name but exclude common non-player objects
                string name = playerObject.name.ToLower();
                if ((name.Contains("player") || name.Contains("character") || name.Contains("avatar")) &&
                    !name.Contains("prefab") && !name.Contains("template") && !name.Contains("model") &&
                    !name.Contains("dummy") && !name.Contains("target") && !name.Contains("screen"))
                {
                    // Additional check: must have some movement component
                    if (playerController != null || playerRigidbody != null)
                    {
                        isRealPlayer = true;
                        return;
                    }
                }

                // Default: not a real player
                isRealPlayer = false;
            }
        }

        // MelonPrefs categories and entries
        private const string CATEGORY_GENERAL = "DoubleJump";
        private const string SETTING_JUMP_FORCE = "JumpForce";
        private const string SETTING_ENABLE_DOUBLE_JUMP = "EnableDoubleJump";
        private const string SETTING_MAX_JUMP_HEIGHT = "MaxJumpHeight";
        private const string SETTING_ALLOW_TRIPLE_JUMP = "AllowTripleJump";
        private const string SETTING_SYNC_CONFIG = "SyncConfig";

        public override void OnInitializeMelon()
        {
            // Register MelonPrefs
            MelonPreferences.CreateCategory(CATEGORY_GENERAL, "Double Jump Settings");
            MelonPreferences.CreateEntry(CATEGORY_GENERAL, SETTING_JUMP_FORCE, config.JumpForce, "Jump Force");
            MelonPreferences.CreateEntry(CATEGORY_GENERAL, SETTING_ENABLE_DOUBLE_JUMP, config.EnableDoubleJump, "Enable Double Jump");
            MelonPreferences.CreateEntry(CATEGORY_GENERAL, SETTING_MAX_JUMP_HEIGHT, config.MaxJumpHeight, "Max Jump Height");
            MelonPreferences.CreateEntry(CATEGORY_GENERAL, SETTING_ALLOW_TRIPLE_JUMP, config.AllowTripleJump, "Allow Triple Jump");
            MelonPreferences.CreateEntry(CATEGORY_GENERAL, SETTING_SYNC_CONFIG, config.SyncConfig, "Sync Config (Host Only)");

            // Load config from file
            LoadConfig();

            // Initialize host values from our local config
            hostJumpForce = config.JumpForce;
            hostDoubleJumpEnabled = config.EnableDoubleJump;
            hostMaxJumpHeight = config.MaxJumpHeight;
            hostTripleJumpAllowed = config.AllowTripleJump;

            // Initialize client values with same defaults
            clientJumpForce = config.JumpForce;
            clientDoubleJumpEnabled = config.EnableDoubleJump;
            clientMaxJumpHeight = config.MaxJumpHeight;
            clientTripleJumpAllowed = config.AllowTripleJump;

            LoggerInstance.Msg($"DoubleJump mod initialized with jump force: {config.JumpForce}");
            LoggerInstance.Msg("Config will be automatically synced in multiplayer if you're the host");
            LoggerInstance.Msg("If you need any help join https://discord.gg/PCawAVnhMH");
            LoggerInstance.Msg("Happy Selling!");

            // Try to get local player's Steam ID
            try
            {
                localSteamID = SteamUser.GetSteamID();
            }
            catch (System.Exception)
            {
                // Silent catch
            }

            // Register for Steam callbacks
            SteamCallbacks.RegisterCallbacks();
        }

        private void LoadConfig()
        {
            try
            {
                // Create directory if it doesn't exist
                if (!Directory.Exists(CONFIG_DIRECTORY))
                {
                    Directory.CreateDirectory(CONFIG_DIRECTORY);
                }

                // If config file exists, load it
                if (File.Exists(CONFIG_FILE))
                {
                    string json = File.ReadAllText(CONFIG_FILE);
                    config = ModConfig.FromJson(json);

                    // Update MelonPrefs to match loaded config
                    MelonPreferences.SetEntryValue(CATEGORY_GENERAL, SETTING_JUMP_FORCE, config.JumpForce);
                    MelonPreferences.SetEntryValue(CATEGORY_GENERAL, SETTING_ENABLE_DOUBLE_JUMP, config.EnableDoubleJump);
                    MelonPreferences.SetEntryValue(CATEGORY_GENERAL, SETTING_MAX_JUMP_HEIGHT, config.MaxJumpHeight);
                    MelonPreferences.SetEntryValue(CATEGORY_GENERAL, SETTING_ALLOW_TRIPLE_JUMP, config.AllowTripleJump);
                    MelonPreferences.SetEntryValue(CATEGORY_GENERAL, SETTING_SYNC_CONFIG, config.SyncConfig);

                    LoggerInstance.Msg("Config loaded from file");
                }
                else
                {
                    // If no config file exists, create one with default values
                    config = new ModConfig();
                    SaveConfig();
                    LoggerInstance.Msg("Created new config file with default values");
                }
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Error loading config: {ex.Message}");
                // Use default config if loading fails
                config = new ModConfig();
            }
        }

        private void SaveConfig()
        {
            try
            {
                // Update config from MelonPrefs
                config.JumpForce = MelonPreferences.GetEntryValue<float>(CATEGORY_GENERAL, SETTING_JUMP_FORCE);
                config.EnableDoubleJump = MelonPreferences.GetEntryValue<bool>(CATEGORY_GENERAL, SETTING_ENABLE_DOUBLE_JUMP);
                config.MaxJumpHeight = MelonPreferences.GetEntryValue<float>(CATEGORY_GENERAL, SETTING_MAX_JUMP_HEIGHT);
                config.AllowTripleJump = MelonPreferences.GetEntryValue<bool>(CATEGORY_GENERAL, SETTING_ALLOW_TRIPLE_JUMP);
                config.SyncConfig = MelonPreferences.GetEntryValue<bool>(CATEGORY_GENERAL, SETTING_SYNC_CONFIG);

                // Create directory if it doesn't exist
                if (!Directory.Exists(CONFIG_DIRECTORY))
                {
                    Directory.CreateDirectory(CONFIG_DIRECTORY);
                }

                // Save config to file using our custom JSON serializer
                string json = config.ToJson();
                File.WriteAllText(CONFIG_FILE, json);

                // Also save MelonPrefs for compatibility
                MelonPreferences.Save();

                LoggerInstance.Msg("Config saved to file");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Error saving config: {ex.Message}");
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
            {
                FindAllPlayers();
                DetermineIfHost();
                isInitialized = true;
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
                // Try to determine if we're the host
                // Method 1: Check if we're in a lobby and are the owner
                if (SteamMatchmaking.GetLobbyOwner(SteamMatchmaking.GetLobbyByIndex(0)).m_SteamID == localSteamID.m_SteamID)
                {
                    isHost = true;
                    LoggerInstance.Msg("You are the host - config sync enabled");
                    return;
                }

                // Method 2: Check for host-specific objects or components in the scene
                // This is game-specific and would need to be customized

                // Method 3: Check if we're the first player or have a specific name
                foreach (PlayerData player in players)
                {
                    if (player.isLocalPlayer)
                    {
                        string name = player.playerObject.name.ToLower();
                        if (name.Contains("host") || name.Contains("server") || name.Contains("owner"))
                        {
                            isHost = true;
                            player.isHost = true;
                            LoggerInstance.Msg("You are the host - config sync enabled");
                            return;
                        }
                    }
                }

                isHost = false;
                LoggerInstance.Msg("You are a client - waiting for host config");
            }
            catch (System.Exception)
            {
                // If we can't determine, assume we're not the host
                isHost = false;
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
            if (!isHost || !config.SyncConfig) return;

            try
            {
                // Only sync the gameplay-affecting values
                string configData = $"{CONFIG_MESSAGE_PREFIX}{hostJumpForce}|{hostDoubleJumpEnabled}|{hostMaxJumpHeight}|{hostTripleJumpAllowed}";

                // Send to all players in the lobby
                CSteamID lobbyId = SteamMatchmaking.GetLobbyByIndex(0);
                int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyId);

                for (int i = 0; i < memberCount; i++)
                {
                    CSteamID memberId = SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i);
                    if (memberId.m_SteamID != localSteamID.m_SteamID) // Don't send to self
                    {
                        SteamNetworking.SendP2PPacket(memberId, Encoding.UTF8.GetBytes(configData), (uint)configData.Length, EP2PSend.k_EP2PSendReliable);
                    }
                }

                configSynced = true;
                LoggerInstance.Msg("Jump values synced to all clients");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Failed to sync config: {ex.Message}");
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
                        // Only update the client-side gameplay values, never touch our config
                        clientJumpForce = float.Parse(parts[0]);
                        clientDoubleJumpEnabled = bool.Parse(parts[1]);
                        clientMaxJumpHeight = float.Parse(parts[2]);
                        clientTripleJumpAllowed = bool.Parse(parts[3]);

                        MelonLogger.Msg($"Received host jump values: force={clientJumpForce}, enabled={clientDoubleJumpEnabled}, " +
                                       $"maxHeight={clientMaxJumpHeight}, tripleJump={clientTripleJumpAllowed}");
                        configSynced = true;
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"Failed to process jump values: {ex.Message}");
                    // On failure, fall back to our local values
                    clientJumpForce = config.JumpForce;
                    clientDoubleJumpEnabled = config.EnableDoubleJump;
                    clientMaxJumpHeight = config.MaxJumpHeight;
                    clientTripleJumpAllowed = config.AllowTripleJump;
                }
            }
        }

        // Handle config changes from the MelonPrefs menu
        public override void OnPreferencesSaved()
        {
            bool configChanged = false;

            // Update config and host values, never client values
            float newJumpForce = MelonPreferences.GetEntryValue<float>(CATEGORY_GENERAL, "JumpForce");
            if (newJumpForce != config.JumpForce)
            {
                config.JumpForce = newJumpForce;
                hostJumpForce = newJumpForce; // Update host value only
                configChanged = true;
            }

            bool newDoubleJumpEnabled = MelonPreferences.GetEntryValue<bool>(CATEGORY_GENERAL, "EnableDoubleJump");
            if (newDoubleJumpEnabled != config.EnableDoubleJump)
            {
                config.EnableDoubleJump = newDoubleJumpEnabled;
                hostDoubleJumpEnabled = newDoubleJumpEnabled; // Update host value only
                configChanged = true;
            }

            float newMaxJumpHeight = MelonPreferences.GetEntryValue<float>(CATEGORY_GENERAL, "MaxJumpHeight");
            if (newMaxJumpHeight != config.MaxJumpHeight)
            {
                config.MaxJumpHeight = newMaxJumpHeight;
                hostMaxJumpHeight = newMaxJumpHeight; // Update host value only
                configChanged = true;
            }

            bool newTripleJumpAllowed = MelonPreferences.GetEntryValue<bool>(CATEGORY_GENERAL, "AllowTripleJump");
            if (newTripleJumpAllowed != config.AllowTripleJump)
            {
                config.AllowTripleJump = newTripleJumpAllowed;
                hostTripleJumpAllowed = newTripleJumpAllowed; // Update host value only
                configChanged = true;
            }
            if (configChanged)
            {
                SaveConfig();

                if (isHost && config.SyncConfig)
                {
                    configSynced = false;
                    lastConfigSyncTime = 0f; // Force immediate sync
                    LoggerInstance.Msg("Config changed - will sync to clients");
                }
            }
        }

        // Handle game quit
        public override void OnApplicationQuit()
        {
            // Save any pending config changes
            SaveConfig();
        }
    }
}
