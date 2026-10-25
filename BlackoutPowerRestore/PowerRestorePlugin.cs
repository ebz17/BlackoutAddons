using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using CommonAssets.Scripts.Game;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace BlackoutPowerRestore
{
    public enum BlackDivisionTiming
    {
        BeforeTheBlackout,
        AfterTheBlackout,
    }

    public enum AnnouncementLanguage
    {
        Russian,
        English,
    }

    [BepInPlugin("com.blackoutaddons.powerrestore", "Blackout Power Restore", "1.27.10")]
    [BepInDependency("com.vultify.blackout")]
    [BepInDependency("xyz.drakia.bigbrain", BepInDependency.DependencyFlags.SoftDependency)]
    public class PowerRestorePlugin : BaseUnityPlugin
    {
        private const string LogTag = "[PowerRestore]";
        private const string SwitchName = "blackout_power_restore_switch";
        private const string DonorExfil = "lab_Parking_Gate";
        private const string AutoPanelName = "Boiler_Control_Panel_A";
        private const string TipStart = "Start main generator";
        private const string TipStop = "Shut down main generator";

        // Basement generator room panel (placed in game with the placement key).
        private const string DefaultSwitchPosition = "-231.255, -2.73, -300.75";   // boiler control panel, basement
        private const string DefaultSwitchRotation = "0, 90, 0";
        private const string DefaultPartSpots = "-183.159, 0.079, -326.552; -175.721, 0.079, -327.651; -170.819, 0.087, -319.317; -168.622, 0.07, -309.986; -186.03, 0.061, -328.464; -180.994, 0.061, -328.924; -166.638, 0.074, -317.816";
        private const string DefaultBeaconSpots = "-133.317, 0.068, -337.113; -166.89, 0.068, -351.937; -164.217, 0.068, -379.416; -216.937, 1.411, -368.63; -228.221, 0.106, -366.066; -261.379, 0.068, -343.924; -235.098, 4.114, -341.241; -221.228, 4.174, -314.689; -231.231, 0.092, -380.565; -231.079, 0.069, -403.579; -231.575, 0.085, -424.571; -231.641, 1.435, -442.034; -157.335, 0.078, -341.468; -144.749, 4.113, -327.835; -153.153, 4.113, -298.295; -183.139, 4.166, -298.334; -178.521, 4.187, -281.837; -163.296, 4.17, -281.467; -192.512, 1.474, -280.259; -167.284, 0.045, -260.443; -167.942, 0.064, -276.316; -170.549, 0.086, -299.294; -177.352, 0.039, -322.596; -183.076, 0.083, -341.423; -177.754, 2.323, -369.205; -215.042, -3.989, -304.885; -117.417, 0.036, -350.556; -118.007, 0.036, -375.175; -126.738, 0.033, -390.262; -117.576, 0.033, -399.669; -129.429, 0.033, -408.807; -115.982, 0.085, -430.464; -157.221, 0.051, -419.164; -151.026, 4.162, -400.511; -183.393, 4.119, -388.854; -194.005, 4.103, -405.721; -196.292, 4.151, -372.319; -256.407, -3.977, -297.606; -245.031, -3.977, -319.726";
        private const string DefaultCabinets = "-232.741, -2.442, -295.317; -212.386, -2.67, -307.716";   // switchgear room breaker cabinet (placed in game)
        private const string DefaultGenA = "-226.539, -2.516, -299.328";   // basement generators (placed in game)
        private const string DefaultGenB = "-218.662, -2.491, -299.528";

        // Config
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _indicatorLight;
        private ConfigEntry<bool> _announcement;
        private ConfigEntry<bool> _allowCutAgain;
        private ConfigEntry<bool> _staged;
        private ConfigEntry<int> _waves;
        private ConfigEntry<float> _generatorStart;
        private ConfigEntry<float> _betweenWaves;
        private ConfigEntry<bool> _flicker;
        private ConfigEntry<bool> _voice;
        private ConfigEntry<float> _soundVolume;
        private ConfigEntry<float> _voiceVolume;
        private ConfigEntry<AnnouncementLanguage> _language;
        private ConfigEntry<bool> _siren;
        private ConfigEntry<bool> _beaconsCfg;
        private ConfigEntry<int> _failChance;
        private ConfigEntry<int> _maxFails;
        private ConfigEntry<bool> _noise;
        private ConfigEntry<float> _noiseRange;
        private ConfigEntry<bool> _restoreExtracts;
        private ConfigEntry<bool> _lampFlicker;
        private ConfigEntry<bool> _paSpeakers;
        private ConfigEntry<string> _gameIntruders, _gameLightsOut;
        private ConfigEntry<KeyboardShortcut> _locateKey;
        private ConfigEntry<KeyboardShortcut> _placeKey;
        private ConfigEntry<string> _switchPosition;
        private ConfigEntry<string> _switchRotation;

        private BlackoutBridge _bo;
        private readonly AmbientSnapshot _ambient = new AmbientSnapshot();
        private SoundBank _sound;
        private StagedRestore _stagedRestore;
        private readonly System.Collections.Generic.List<AudioSource> _generatorLoops = new System.Collections.Generic.List<AudioSource>();
        private ConfigEntry<KeyboardShortcut> _placeGenKey;
        private ConfigEntry<string> _genAPos;
        private ConfigEntry<string> _genBPos;
        private int _nextGenSlot;
        private List<Vector3> _autoGens;              // generators found in the scene (if not placed by hand)

        // switchgear faults / partial power
        private ConfigEntry<int> _tripChance;
        private ConfigEntry<int> _faultCount;
        private ConfigEntry<string> _panelPositions;
        private ConfigEntry<KeyboardShortcut> _placePanelKey;
        private ConfigEntry<KeyboardShortcut> _clearPanelsKey;
        private FaultPanels _faults;
        private bool _panelHintShown;

        private ConfigEntry<bool> _surprise;
        private ConfigEntry<int> _burnoutChance;
        private ConfigEntry<bool> _falseStarts;
        private ConfigEntry<float> _falseStartMin;
        private ConfigEntry<float> _falseStartOnMin, _falseStartOnMax;
        private BurntSwitch _burnt;
        private float _nextFalseStart;
        private ConfigEntry<float> _flickerAmount;
        private ConfigEntry<int> _breakerFailChance;
        private ConfigEntry<float> _breakerFailMin;
        private ConfigEntry<float> _breakerFailMax;
        private ConfigEntry<bool> _bdGenRoom;
        private ConfigEntry<int> _bdGenRoomChance;
        private ConfigEntry<bool> _bdHunt;
        private ConfigEntry<bool> _bdHostileAll;
        private ConfigEntry<bool> _secondWave;
        private ConfigEntry<int> _secondWaveMin, _secondWaveMax;
        private ConfigEntry<float> _secondWaveDelay;
        private ConfigEntry<bool> _gateSquad;
        private ConfigEntry<int> _gateSquadCount;
        private ConfigEntry<BlackDivisionTiming> _bdTiming;
        private float _bdReadyAt = -1f;
        private float _nextHoldLog;
        private ConfigEntry<KeyboardShortcut> _placeBdKey;
        private ConfigEntry<string> _bdSpot;
        private ConfigEntry<KeyboardShortcut> _placeKillaKey;
        private ConfigEntry<string> _killaSpot;
        private ConfigEntry<bool> _killaStalks;
        private ConfigEntry<bool> _tagillaOn, _tagillaGuards, _tagillaFlicker, _tagillaRelentless;
        private ConfigEntry<float> _tagillaGuardRange, _tagillaChaseRange;
        private ConfigEntry<bool> _bdUnstick;
        private ConfigEntry<bool> _bdHuntLayer;
        private ConfigEntry<int> _burnoutStep;
        private int _startsThisRaid;      // every start wears the control a bit more
        private ConfigEntry<string> _beaconSpots, _partSpots;
        private ConfigEntry<KeyboardShortcut> _addPartKey, _clearPartsKey;
        private ConfigEntry<KeyboardShortcut> _addBeaconKey, _clearBeaconsKey;
        private ConfigEntry<float> _killaBackup;
        private float _killaReleaseAt = -1f;
        private ConfigEntry<int> _defaultsVersion;
        private ConfigEntry<bool> _emergencyCode, _emergencyGridOn, _repairKitOn, _conduitWithKilla;
        private ConfigEntry<float> _emergencyMin, _emergencyMax;
        private EmergencyGrid _emergency;
        private bool _repairRunning, _repairedReady, _genBDamaged;
        private float _nextWhiteboardCheck;
        private bool _whiteboardHidden;
        private ConfigEntry<KeyboardShortcut> _locateBotsKey;
        private DistributionFault _dist;
        private readonly AlarmBeacons _beacons = new AlarmBeacons();
        private EmergencyFlicker _emergencyFlicker;
        private bool _failedStartRunning;
        private int _failsInRow;
        private readonly System.Random _rng = new System.Random();

        // Per-raid state
        private bool _inRaid;
        private bool _everCut;            // Blackout has cut the power at least once this raid
        private bool _toggleRequested;
        private float _nextToggleAllowed;
        private float _switchResetAt;
        private float _switchCheckAt;
        private bool _selfClosing;        // we are flipping the switch back ourselves
        private Switch _switch;
        private float _nextSnapshot;
        private GameObject _switchGo;
        private Light _indicator;
        private bool _missingPositionWarned;

        // On-screen message
        private string _msg;
        private float _msgUntil;
        private GUIStyle _msgStyle;

        private void Awake()
        {
            _enabled = Config.Bind("1. General", "Enabled", true,
                "Spawn a power switch during Blackout Labs raids that turns the facility lights back on.");
            _indicatorLight = Config.Bind("1. General", "Indicator light", true,
                "Small blinking red light on the switch so you can find it in the dark (turns green when power is back).");
            _allowCutAgain = Config.Bind("1. General", "Switch can cut power again", true,
                "Use the switch again to turn the lights back off (emergency power). Toggle as often as you like.");
            _announcement = Config.Bind("1. General", "Subtitles", false,
                "On-screen text for the PA announcements (this mod's and Blackout's 'emergency power' one). " +
                "Off = voice only, no text boxes.");
            _staged = Config.Bind("3. Power-up sequence", "Staged restore", true,
                "Generator starts up, then the lights come back in waves spreading out from the generator room. Off = everything at once.");
            _waves = Config.Bind("3. Power-up sequence", "Waves", 4,
                new ConfigDescription("How many waves the facility comes back in.", new AcceptableValueRange<int>(1, 8)));
            _generatorStart = Config.Bind("3. Power-up sequence", "Generator start time", 6f,
                new ConfigDescription("Seconds from flipping the switch until the first lights come on.", new AcceptableValueRange<float>(1f, 20f)));
            _betweenWaves = Config.Bind("3. Power-up sequence", "Seconds between waves", 3f,
                new ConfigDescription("Gap between each wave of lights.", new AcceptableValueRange<float>(0.5f, 10f)));
            _flicker = Config.Bind("3. Power-up sequence", "Flicker", true,
                "Lights flicker a few times as they come on, like fluorescent tubes starting.");
            _voice = Config.Bind("3. Power-up sequence", "Voice announcements", true,
                "PA voice: 'generator start sequence initiated' and 'main power restored'.");
            _failChance = Config.Bind("3. Power-up sequence", "Failed start chance", 70,
                new ConfigDescription("Chance (%) the generator cranks but doesn't start, and you have to try again.", new AcceptableValueRange<int>(0, 100)));
            _maxFails = Config.Bind("3. Power-up sequence", "Failed starts before it catches", 4,
                new ConfigDescription("At most this many failed starts in a row - the next try always starts. " +
                                      "Restarting generator B after a trip fails at most once.", new AcceptableValueRange<int>(0, 5)));
            _siren = Config.Bind("3. Power-up sequence", "Alarm siren", true,
                "Mechanical alarm siren wails through the facility while the power comes back.");
            _beaconsCfg = Config.Bind("3. Power-up sequence", "Warning beacons", true,
                "Rotating red warning lights in the corridors (and the generator room) while the siren wails.");
            _tripChance = Config.Bind("3. Power-up sequence", "Generator B trip chance", 85,
                new ConfigDescription("Chance (%) generator B trips during sync: partial power until you reset the breakers in the switchgear room.", new AcceptableValueRange<int>(0, 100)));
            _faultCount = Config.Bind("3. Power-up sequence", "Tripped breaker count", 2,
                new ConfigDescription("How many cabinets need resetting after generator B trips.", new AcceptableValueRange<int>(1, 5)));
            _surprise = Config.Bind("1. General", "Surprise blackout", true,
                "The raid looks like normal Labs (all normal extracts listed and working) until the power actually cuts. " +
                "Off = Blackout's default: extracts locked and hidden from the start.");
            _restoreExtracts = Config.Bind("3. Power-up sequence", "Power restores normal extracts", true,
                "With main power back, the normal Labs extracts work again (with their usual rules). Cutting power locks them again.");
            _lampFlicker = Config.Bind("1. General", "Emergency lamp flicker", true,
                "Battery emergency lamps occasionally stutter, drop out or brown out while the power is off.");
            _flickerAmount = Config.Bind("1. General", "Flicker amount", 1f,
                new ConfigDescription("How much the lights flicker: start-up, old tubes while powered, emergency lamps. 0 = calm, 1 = normal, 2 = a lot.",
                    new AcceptableValueRange<float>(0f, 2f)));
            _burnoutChance = Config.Bind("3. Power-up sequence", "Switch burnout chance", 35,
                new ConfigDescription("Chance (%) per start attempt that the generator control switch shorts out (generator B restarts: half). " +
                                      "Everything goes dark until it's repaired (repair kit) - or use Wedge's key on the gate switch.",
                    new AcceptableValueRange<int>(0, 100)));
            _burnoutStep = Config.Bind("3. Power-up sequence", "Burnout increase per start", 10,
                new ConfigDescription("Every start attempt this raid adds this much (%) to the burnout chance (max 90%). " +
                                      "A repaired control starts fresh again.", new AcceptableValueRange<int>(0, 30)));
            _falseStarts = Config.Bind("3. Power-up sequence", "False starts after burnout", true,
                "With the control switch burnt out, the generators keep trying to start on their own: the lights surge back " +
                "flickering for a few seconds, then die again. Extracts and keycards stay locked.");
            _falseStartMin = Config.Bind("3. Power-up sequence", "False start interval (min)", 1.5f,
                new ConfigDescription("Minutes of darkness between false starts (plus a few seconds of randomness).", new AcceptableValueRange<float>(0.25f, 10f)));
            _falseStartOnMin = Config.Bind("3. Power-up sequence", "False start lights on (s, min)", 15f,
                new ConfigDescription("Shortest time the lights stay on during a false start (random between min and max).", new AcceptableValueRange<float>(5f, 30f)));
            _falseStartOnMax = Config.Bind("3. Power-up sequence", "False start lights on (s, max)", 30f,
                new ConfigDescription("Longest time the lights stay on during a false start.", new AcceptableValueRange<float>(5f, 30f)));
            _breakerFailChance = Config.Bind("3. Power-up sequence", "Breaker failure chance", 85,
                new ConfigDescription("Chance (%) that, some minutes after full power, the sector B breaker trips. Sector B goes dark, auto-reclose keeps " +
                                      "trying (lights on for a few seconds, then out again) until you reset the cabinet in the switchgear room.",
                    new AcceptableValueRange<int>(0, 100)));
            _breakerFailMin = Config.Bind("3. Power-up sequence", "Breaker failure earliest (min)", 3f,
                new ConfigDescription("Earliest the breaker can fail after full power.", new AcceptableValueRange<float>(0.5f, 30f)));
            _breakerFailMax = Config.Bind("3. Power-up sequence", "Breaker failure latest (min)", 10f,
                new ConfigDescription("Latest the breaker can fail after full power.", new AcceptableValueRange<float>(0.5f, 60f)));
            _bdGenRoom = Config.Bind("5. AI", "Black Division spawns in generator room", true,
                "Blackout raids: Wedge and his Black Division guards start in the basement generator room (they caused the blackout). " +
                "Other bots spawn as normal. Needs the server mod (spawnInGeneratorRoom = true).");
            _bdGenRoomChance = Config.Bind("5. AI", "Generator room spawn chance", 100,
                new ConfigDescription("Chance (%) they start in the generator room. Otherwise a random Labs spot.", new AcceptableValueRange<int>(0, 100)));
            _bdTiming = Config.Bind("5. AI", "Black Division arrives", BlackDivisionTiming.BeforeTheBlackout,
                "BeforeTheBlackout: they arrive in the generator room at raid start and shut the generators down - the lights go out " +
                "once they're in. AfterTheBlackout: the lights go out first, they arrive a few seconds later.");
            _bdHunt = Config.Bind("5. AI", "Black Division hunts", true,
                "After spawning, Black Division moves out and hunts whoever is closest (you, PMC bots, scavs, raiders): " +
                "every 30-60 s they get a rough idea where their nearest target is and go check.");
            _bdHostileAll = Config.Bind("5. AI", "Black Division hostile to everyone", true,
                "Blackout raids: Black Division attacks everyone on sight (no warning USECs or scavs first), and everyone fights back.");
            _secondWave = Config.Bind("5. AI", "Second wave", true,
                "Once per raid, when you get full power back or open the gates with the key (whichever comes first), a small Black Division " +
                "squad comes back in somewhere in Labs away from you. They hunt like the first team.");
            _gateSquad = Config.Bind("5. AI", "Gate squad", true,
                "When you open the gates with Wedge's key, Black Division sends soldiers in through them to hold the hangar and parking gates " +
                "for the rest of the raid. They fight anyone they see and go back to their gate. On top of the second wave.");
            _gateSquadCount = Config.Bind("5. AI", "Gate squad size", 2,
                new ConfigDescription("Soldiers holding the gates, spread over both (2 = one at each gate).", new AcceptableValueRange<int>(1, 6)));
            _secondWaveMin = Config.Bind("5. AI", "Second wave size (min)", 3,
                new ConfigDescription("Fewest Black Division in the second wave.", new AcceptableValueRange<int>(1, 6)));
            _secondWaveMax = Config.Bind("5. AI", "Second wave size (max)", 3,
                new ConfigDescription("Most Black Division in the second wave.", new AcceptableValueRange<int>(1, 6)));
            _secondWaveDelay = Config.Bind("5. AI", "Second wave delay (s)", 30f,
                new ConfigDescription("About how long after full power / the gates opening they arrive (+/- 20%).", new AcceptableValueRange<float>(5f, 300f)));
            _killaStalks = Config.Bind("5. AI", "Killa stalks you", true,
                "Blackout raids (server mod: Killa through the sewage conduit): every 45-90 s Killa gets a rough idea (~20 m) of where someone is " +
                "and comes to check - every other time you, otherwise whoever is closest (PMCs, Black Division, raiders). Off = he roams on his own.");
            _tagillaOn = Config.Bind("5. AI", "Tagilla comes with Killa", true,
                "Blackout raids (server mod 1.6+): Tagilla (Shadow of Tagilla by default - server config tagillaType) comes through the sewage conduit right behind Killa. Off = he stays out.");
            _tagillaGuards = Config.Bind("5. AI", "Tagilla guards the sewage exit", true,
                "Tagilla stays by the sewage conduit and charges anyone who gets close. While Killa lives he always guards; once Killa " +
                "is dead he keeps guarding only if the conduit is the only way out - otherwise he leaves and hunts you. " +
                "Needs BigBrain (game restart after changing). Off = normal Tagilla.");
            _tagillaGuardRange = Config.Bind("5. AI", "Tagilla guard range (m)", 20f,
                new ConfigDescription("Get this close to the sewage exit and he charges.", new AcceptableValueRange<float>(5f, 60f)));
            _tagillaChaseRange = Config.Bind("5. AI", "Tagilla chase range (m)", 40f,
                new ConfigDescription("Only with 'Tagilla chases until you die' off: how far from the exit he chases before he walks back to his post.", new AcceptableValueRange<float>(10f, 120f)));
            _tagillaRelentless = Config.Bind("5. AI", "Tagilla chases until you die", true,
                "Once Tagilla charges you he doesn't give up or go back to the exit: he keeps coming until you (or he) are dead. " +
                "Off = he gives up at the chase range and walks back to the exit.");
            _tagillaFlicker = Config.Bind("5. AI", "Lights flicker near Tagilla", true,
                "While Tagilla is after someone, the emergency lamps and red beacons around him stutter and drop out. A dying amber lamp hangs over the sewage exit while he guards it.");
            _killaBackup = Config.Bind("5. AI", "Killa backup time (min)", 15f,
                new ConfigDescription("Surprise Killa comes 30-60 s after Black Division is wiped out - or at this raid time if they're still alive.",
                    new AcceptableValueRange<float>(1f, 60f)));
            _bdHuntLayer = Config.Bind("5. AI", "Black Division really hunts (restart)", true,
                "Black Division uses the game's raider brain: it picks a spot and holds it. On: in blackout raids their raider 'hold' layer is " +
                "taken out (other maps untouched) and a small hunt layer walks them to where the hunt orders send them, until they see someone. " +
                "Needs BigBrain (Black Division requires it anyway). The hunt layer itself needs a game restart.");
            _bdUnstick = Config.Bind("5. AI", "Unstick Black Division", true,
                "A Black Division soldier that hasn't moved for 2 minutes (40 s right after spawning) - sees nobody, you 40+ m away - is moved a couple of metres to free floor.");
            _noise = Config.Bind("5. AI", "Noise attracts bots", true,
                "The generator (and the siren) are loud - bots may come to the generator room to investigate.");
            _noiseRange = Config.Bind("5. AI", "Noise range", 1f,
                new ConfigDescription("Multiplier for how far bots hear it (1 = generator ~140 m, siren ~220 m).", new AcceptableValueRange<float>(0.1f, 2f)));
            _language = Config.Bind("4. Sound", "Announcement language", AnnouncementLanguage.Russian,
                "PA voice language. Subtitles are always English.");
            _paSpeakers = Config.Bind("4. Sound", "Use facility PA speakers", true,
                "Announcements also play through Labs' own ceiling speakers, like the game's announcer.");
            const string clipHelp = " The game's own Labs announcer (from your game files). Clip names from 'Announcer scan' in the log; " +
                                    "'a+b+c' plays them one after another, 'b|y' picks one at random. Empty = off.";
            _gameLightsOut = Config.Bind("4. Sound", "Game voice: lights out", "announcer_lights_out_01",
                "Played when the power goes out, instead of Blackout's own announcement." + clipHelp);
            _gameIntruders = Config.Bind("4. Sound", "Game voice: intruders",
                "announcer_composite_1_security+announcer_composite_2_B|announcer_composite_2_Y+announcer_composite_3_11",
                "Played after Black Division cuts the power ('Attention, security! Unauthorized activity in sector B-11')." + clipHelp);
            _soundVolume = Config.Bind("4. Sound", "Effects volume", 0.8f,
                new ConfigDescription("Generator, breakers, power surge.", new AcceptableValueRange<float>(0f, 1f)));
            _voiceVolume = Config.Bind("4. Sound", "Voice volume", 1f,
                new ConfigDescription("PA announcements.", new AcceptableValueRange<float>(0f, 1f)));

            _placeKey = Config.Bind("2. Placement", "Place switch key", new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl),
                "In any Labs raid: look at the wall/panel where you want the switch and press this. The spot is saved.");
            _switchPosition = Config.Bind("2. Placement", "Switch position", DefaultSwitchPosition,
                "x, y, z of the switch. Default = basement generator room. Set with the placement key. " +
                "Empty = automatic (on the Boiler_Control_Panel_A panel).");
            _placeGenKey = Config.Bind("2. Placement", "Place generator key", new KeyboardShortcut(KeyCode.F11, KeyCode.LeftControl),
                "Look at a generator and press: 1st press = generator A (starts first), 2nd = generator B, then A again...");
            _genAPos = Config.Bind("2. Placement", "Generator A position", DefaultGenA,
                "Where generator A's sound comes from (default: the first generator in the basement). Empty = found automatically / at the switch.");
            _genBPos = Config.Bind("2. Placement", "Generator B position", DefaultGenB,
                "Where generator B's sound comes from (starts a few seconds after A). Empty = only one generator.");
            _placePanelKey = Config.Bind("2. Placement", "Set breaker cabinet key", new KeyboardShortcut(KeyCode.F7, KeyCode.LeftControl),
                "Look at the cabinet and press: this becomes THE breaker cabinet (replaces the saved one).");
            _clearPanelsKey = Config.Bind("2. Placement", "Add extra breaker cabinet key", new KeyboardShortcut(KeyCode.F7, KeyCode.LeftControl, KeyCode.LeftShift),
                "Optional: look at another cabinet and press to add it as an extra place where a breaker can trip.");
            _panelPositions = Config.Bind("2. Placement", "Breaker cabinet", DefaultCabinets,
                "x, y, z of the breaker cabinet (extra cabinets separated by ';'). Set with the key.");
            _placeBdKey = Config.Bind("2. Placement", "Place Black Division spawn key", new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl),
                "Stand where Black Division should spawn (generator room floor) and press. Saves where you stand.");
            _bdSpot = Config.Bind("2. Placement", "Black Division spawn spot", "",
                "x, y, z where Black Division spawns. Empty = automatic (open floor between the generators and the switch).");
            _placeKillaKey = Config.Bind("2. Placement", "Place Killa spawn key", new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl, KeyCode.LeftShift),
                "Stand where Killa should come in and press. Saves where you stand.");
            _killaSpot = Config.Bind("2. Placement", "Killa spawn spot", "",
                "x, y, z where Killa comes in. Empty = automatic (the sewage conduit extract).");
            _addPartKey = Config.Bind("2. Placement", "Add repair part spot key", new KeyboardShortcut(KeyCode.F2, KeyCode.LeftControl),
                "Stand where a repair part (toolset / bundle of wires) may lie and press. Each raid the toolset and the 2 wires go on " +
                "random spots from your list (different ones while there are enough). Takes effect next raid.");
            _clearPartsKey = Config.Bind("2. Placement", "Clear repair part spots key", new KeyboardShortcut(KeyCode.F2, KeyCode.LeftControl, KeyCode.LeftShift),
                "Removes your repair part spots (back to automatic: by the computers).");
            _partSpots = Config.Bind("2. Placement", "Repair part spots", DefaultPartSpots,
                "x, y, z; x, y, z ... spots for the toolset and the wires (7 by default). Empty = automatic (next to the PCs in the room with the most computers).");
            _addBeaconKey = Config.Bind("2. Placement", "Add emergency beacon key", new KeyboardShortcut(KeyCode.F3, KeyCode.LeftControl),
                "Stand in a room and press: a red emergency beacon goes on the ceiling above you whenever emergency power is on.");
            _clearBeaconsKey = Config.Bind("2. Placement", "Clear emergency beacons key", new KeyboardShortcut(KeyCode.F3, KeyCode.LeftControl, KeyCode.LeftShift),
                "Removes all the beacon spots you placed.");
            _beaconSpots = Config.Bind("2. Placement", "Emergency beacon spots", DefaultBeaconSpots,
                "x, y, z; x, y, z ... where you stood when adding beacons (39 by default). Empty = automatic. The gates and the gate switch room always get one.");
            _locateBotsKey = Config.Bind("2. Placement", "Show Killa / Black Division direction key", new KeyboardShortcut(KeyCode.F5, KeyCode.LeftControl),
                "Testing: shows how far and which way Killa, Tagilla (or their bodies) and the nearest Black Division are.");
            _locateKey = Config.Bind("2. Placement", "Show gate switch direction key", new KeyboardShortcut(KeyCode.F6, KeyCode.LeftControl),
                "Shows how far and which way Blackout's gate switch and code whiteboard are (handy for testing).");
            _switchRotation = Config.Bind("2. Placement", "Switch rotation", DefaultSwitchRotation,
                "Rotation (euler x, y, z) of the switch. Set automatically with the placement key.");
            _emergencyCode = Config.Bind("6. Emergency and repair", "Emergency code", false,
                "Blackout's emergency code keypad and whiteboard. Off (default): no power, nothing works - no code.");
            _emergencyGridOn = Config.Bind("6. Emergency and repair", "Emergency power runs out", true,
                "Wedge's key on the gate switch puts Labs on emergency batteries: both gates (and the parking door) open, " +
                "but only for a few minutes. Then the gates close for good (real power brings them back) and the emergency lamps die.");
            _emergencyMin = Config.Bind("6. Emergency and repair", "Emergency power minutes (min)", 3f,
                new ConfigDescription("Shortest time the emergency batteries last (random between min and max).", new AcceptableValueRange<float>(0.5f, 20f)));
            _emergencyMax = Config.Bind("6. Emergency and repair", "Emergency power minutes (max)", 3f,
                new ConfigDescription("Longest time the emergency batteries last.", new AcceptableValueRange<float>(0.5f, 20f)));
            _repairKitOn = Config.Bind("6. Emergency and repair", "Repair kit", true,
                "A burnt generator control can be repaired with a Toolset, a Spark plug and 2 Bundles of wires (plug and wires are used up). " +
                "Toolset and wires lie by the computers in the room with the most PCs; the spark plug is in a Black Division guard's pockets. " +
                "After the repair the generator starts for sure: 50% full power, 50% only generator A (generator B damaged for the raid).");
            _conduitWithKilla = Config.Bind("6. Emergency and repair", "Sewage conduit opens with Killa", true,
                "When Killa breaks the sewage conduit hatch open, that extract opens for the rest of the raid.");
            _defaultsVersion = Config.Bind("9. Internal", "Defaults version", 1, "Don't change - used to update the balance settings once.");
            ApplyNewDefaults();
        }

        /// <summary>
        /// 1.17.1 "brutal blackout" balance: existing config files keep their old numbers otherwise, so the
        /// balance settings are moved to the new defaults once. Changes made after that are kept.
        /// </summary>
        private void ApplyNewDefaults()
        {
            if (_defaultsVersion.Value >= 3)
            {
                if (_defaultsVersion.Value < 5)
                {
                    // 1.21.1: emergency power is a fixed 3 minutes
                    _emergencyMin.Value = 3f;
                    _emergencyMax.Value = 3f;
                    _defaultsVersion.Value = 5;
                    Config.Save();
                    Logger.LogInfo($"{LogTag} Emergency power set to the new default: 3 minutes.");
                }
                if (_defaultsVersion.Value < 6)
                {
                    // 1.25.0: Killa's backup time 25 -> 15 min (unless it was changed by hand)
                    if (Mathf.Approximately(_killaBackup.Value, 25f)) _killaBackup.Value = 15f;
                    _defaultsVersion.Value = 6;
                    Config.Save();
                    Logger.LogInfo($"{LogTag} Killa backup time: {_killaBackup.Value:0} min.");
                }
                return;
            }
            void Reset<T>(ConfigEntry<T> e) => e.Value = (T)e.DefaultValue;
            Reset(_failChance);
            Reset(_maxFails);
            Reset(_burnoutChance);
            Reset(_tripChance);
            Reset(_faultCount);
            Reset(_breakerFailChance);
            Reset(_breakerFailMin);
            Reset(_breakerFailMax);
            Reset(_secondWaveMax);
            if (_secondWaveMin.Value > _secondWaveMax.Value) _secondWaveMin.Value = _secondWaveMax.Value;
            _emergencyMin.Value = 3f;
            _emergencyMax.Value = 3f;
            Reset(_killaBackup);
            _defaultsVersion.Value = 6;
            Config.Save();
            Logger.LogInfo($"{LogTag} Balance settings updated to the 1.17.1 defaults (brutal power restore).");
        }

        private void Start()
        {
            _bo = BlackoutBridge.Create(Logger);
            _sound = new SoundBank(this, Logger);
            StartCoroutine(_sound.LoadFolderAsync(Path.Combine(Path.GetDirectoryName(Info.Location) ?? ".", "sounds")));
            StagedRestore.Plugin = this;
            _emergencyFlicker = new EmergencyFlicker(this);
            _burnt = new BurntSwitch(_sound);
            KeycardGuard.ForceDark = () => _burnt != null && _burnt.Active;
            _faults = new FaultPanels(_sound, Logger)
            {
                OnAllReset = () =>
                {
                    if (DistFault)
                    {
                        _dist.Resolve(_restoreExtracts.Value);
                        if (_announcement.Value) Show("Breaker reset - sector B back on", 5f);
                        return;
                    }
                    // short "ready" cue at the generator switch instead of an announcement
                    _sound.Play3D("cue_ready", SwitchPos, 1f, 2f, 25f);
                    Show("All breakers reset - now restart generator B at the generator room switch", 7f);   // a hint, not a PA subtitle
                },
            };
            KeycardGuard.IsPartial = () => Partial;
            BlackDivHunt.Init(Logger);
            RaidReport.Install(Logger);
            SecondWave.Init(Logger);
            GateSquad.Init(Logger);
            KillaStalk.Init(Logger);
            HuntGoals.Enabled = () => _enabled.Value && _bdHunt.Value && _bo != null && _bo.Ok && _bo.EventThisRaid && BlackDivHunt.MayHunt();
            BlackDivHunt.StripCamp = () => _bdHuntLayer.Value && _enabled.Value && _bdHunt.Value && _bo != null && _bo.Ok && _bo.EventThisRaid;
            if (_bdHuntLayer.Value) InstallHuntLayer();
            KillaStalk.Enabled = () => _killaStalks.Value;
            KillaStalk.OnArrive = KillaEntrance;
            TagillaGuard.Init(Logger);
            TagillaGuard.Enabled = () => _tagillaGuards.Value;
            TagillaGuard.GuardRange = () => _tagillaGuardRange.Value;
            TagillaGuard.Relentless = () => _tagillaRelentless.Value;
            TagillaGuard.ChaseRange = () => Mathf.Max(_tagillaChaseRange.Value, _tagillaGuardRange.Value + 5f);
            TagillaGuard.FallbackPost = () => WedgeSpawn.KillaSpot();
            TagillaGuard.OnArrive = TagillaEntrance;
            WedgeSpawn.TagillaEnabled = () => _tagillaOn.Value;
            DangerFlicker.Enabled = () => _tagillaFlicker.Value;
            PostLamp.Enabled = () => _tagillaFlicker.Value;
            PostLamp.Plugin = this;
            if (_tagillaGuards.Value) InstallTagillaLayer();
            WedgeSpawn.KillaEnabled = () => _enabled.Value && Singleton<GameWorld>.Instance?.LocationId == "laboratory";
            WedgeSpawn.KillaSpot = () => TryParseVector(_killaSpot.Value, out var k) ? k : ExtractManager.PositionOf("lab_Under_Storage_Collector");
            SecondWave.Enabled = () => _enabled.Value && _secondWave.Value && _bo != null && _bo.Ok && _bo.EventThisRaid &&
                                       Singleton<GameWorld>.Instance?.LocationId == "laboratory";
            SecondWave.MinCount = () => _secondWaveMin.Value;
            SecondWave.MaxCount = () => _secondWaveMax.Value;
            SecondWave.Delay = () => _secondWaveDelay.Value;
            SecondWave.OnArrive = () => PlayGameLine(_gameIntruders.Value, 2f);
            GateSquad.Enabled = () => _enabled.Value && _gateSquad.Value && _bo != null && _bo.Ok && _bo.EventThisRaid &&
                                      Singleton<GameWorld>.Instance?.LocationId == "laboratory";
            GateSquad.Count = () => _gateSquadCount.Value;
            ExtractManager.KeyUsed = () => _bo != null && _bo.GatesActivated;
            GateSquad.OnArrive = () => PlayGameLine(_gameIntruders.Value, 2f);
            if (_gateSquad.Value) InstallGateGuardLayer();
            GameAnnouncer.Init(Logger);

            SainBridge.Init(Logger);
            WedgeSpawn.PowerCut = () => _bo != null && _bo.Ok && (_bo.BlackoutActive || _everCut);
            PresenceLights.Log = m => Logger.LogInfo($"{LogTag} {m}");
            WedgeSpawn.Enabled = () => _enabled.Value && _bdGenRoom.Value && Singleton<GameWorld>.Instance?.LocationId == "laboratory";
            WedgeSpawn.Chance = () => _bdGenRoomChance.Value;
            WedgeSpawn.HoldUntilCut = () => _bdTiming.Value == BlackDivisionTiming.AfterTheBlackout;
            WedgeSpawn.PlayerNearRoom = () =>
            {
                var c = WedgeSpawn.RoomCenter();
                var me = Singleton<GameWorld>.Instance?.MainPlayer;
                return c.HasValue && me != null && (me.Position - c.Value).sqrMagnitude < 40f * 40f;
            };
            WedgeSpawn.PlayerPos = () => Singleton<GameWorld>.Instance?.MainPlayer?.Position;
            BlackDivHunt.MayHunt = () => _bo != null && _bo.Ok && (_bo.BlackoutActive || _everCut);
            WedgeSpawn.HandSpot = () => TryParseVector(_bdSpot.Value, out var v) ? v : (Vector3?)null;
            WedgeSpawn.RoomHints = () =>
            {
                var l = new List<Vector3>();
                if (TryParseVector(_genAPos.Value, out var a)) l.Add(a);
                if (TryParseVector(_genBPos.Value, out var b)) l.Add(b);
                if (TryParseVector(_switchPosition.Value, out var sw)) l.Add(sw);
                return l;
            };
            if (_bo != null && _bo.Ok)
            {
                _stagedRestore = new StagedRestore(_bo, _sound, _ambient, Logger);
                _emergency = new EmergencyGrid(_bo, _sound, Logger)
                {
                    Enabled = () => _emergencyGridOn.Value,
                    MinMinutes = () => _emergencyMin.Value,
                    MaxMinutes = () => _emergencyMax.Value,
                    Language = () => Lang,
                    Voice = () => _voice.Value,
                    Show = (t, s) => Show(t, s),
                    Beacons = () => _beaconsCfg.Value,
                    BeaconSpots = () =>
                    {
                        var l = new List<Vector3>();
                        foreach (var part in (_beaconSpots.Value ?? "").Split(';'))
                            if (TryParseVector(part, out var v)) l.Add(v);
                        return l;
                    },
                };
                RepairKit.Init(Logger);
                RepairKit.PartSpots = () =>
                {
                    var l = new List<Vector3>();
                    foreach (var part in (_partSpots.Value ?? "").Split(';'))
                        if (TryParseVector(part, out var v)) l.Add(v);
                    return l;
                };
                KeycardGuard.AllowCode = () => _emergencyCode.Value;
                _dist = new DistributionFault(_stagedRestore, _sound, _bo, Logger)
                {
                    SpawnCabinetFault = () =>
                    {
                        var spots = PanelSpots();
                        if (spots.Count == 0) return false;
                        _faults.Spawn(spots, 1, FindDonorSwitch());
                        return _faults.Remaining > 0;
                    },
                    CabinetPosition = () => _faults.FirstPosition,
                };
                try
                {
                    var harmony = new Harmony("com.blackoutaddons.powerrestore");
                    ExtractManager.Install(harmony, _bo.BlackoutType, Logger);
                    KeycardGuard.Install(harmony, _bo, Logger);
                    WedgeSpawn.Install(harmony, Logger);
                    MoreBotsFix.Install(harmony, Logger);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"{LogTag} extract hook failed: {ex.Message}");
                }
            }
        }

        private bool SequenceRunning => (_stagedRestore != null && _stagedRestore.Running) || _failedStartRunning || _repairRunning;

        // Russian PA files are no longer shipped (voice licence); anyone who still has them can keep using them
        private string Lang => _language.Value == AnnouncementLanguage.Russian && _sound != null && _sound.Has("vo_start_ru") ? "ru" : "en";

        private void Update()
        {
            try
            {
                _sound?.Tick(Time.deltaTime);
                Tick();
            }
            catch (Exception ex)
            {
                Logger.LogError($"{LogTag} {ex}");
                enabled = false; // stop spamming; the raid continues without this mod
            }
        }

        private void Tick()
        {
            GameWorld gw = Singleton<GameWorld>.Instance;
            if (gw == null || gw.MainPlayer == null)
            {
                if (_inRaid) ResetRaid();
                return;
            }

            _inRaid = true;
            if (gw.LocationId != "laboratory" || _bo == null || !_bo.Ok || !_enabled.Value) return;
            RaidReport.Tick(gw);

            if (_placeKey.Value.IsDown()) PlaceSwitchFromView(gw);
            if (_placeGenKey.Value.IsDown()) PlaceGeneratorFromView();
            if (_placePanelKey.Value.IsDown() && !Input.GetKey(KeyCode.LeftShift)) PlacePanelFromView(add: false);
            if (_placeKillaKey.Value.IsDown())
            {
                _killaSpot.Value = FormatVector(gw.MainPlayer.Position);
                Config.Save();
                Logger.LogInfo($"{LogTag} Killa spawn spot saved: {_killaSpot.Value}.");
                Show("Killa spawn spot saved (where you stand)", 4f);
            }
            else if (_placeBdKey.Value.IsDown() && !Input.GetKey(KeyCode.LeftShift))
            {
                _bdSpot.Value = FormatVector(gw.MainPlayer.Position);
                Config.Save();
                Logger.LogInfo($"{LogTag} Black Division spawn spot saved: {_bdSpot.Value}.");
                Show("Black Division spawn spot saved (where you stand)", 4f);
            }
            if (_locateKey.Value.IsDown()) ShowGateDirections(gw.MainPlayer.Position, gw.MainPlayer.Transform.forward);
            if (_clearPartsKey.Value.IsDown())
            {
                _partSpots.Value = "";
                Config.Save();
                Show("Repair part spots cleared (automatic again, from next raid)", 4f);
                Logger.LogInfo($"{LogTag} Repair part spots cleared.");
            }
            else if (_addPartKey.Value.IsDown() && !Input.GetKey(KeyCode.LeftShift))
            {
                string add = FormatVector(gw.MainPlayer.Position);
                _partSpots.Value = string.IsNullOrWhiteSpace(_partSpots.Value) ? add : _partSpots.Value + "; " + add;
                Config.Save();
                int n = _partSpots.Value.Split(';').Length;
                Show($"Repair part spot #{n} added here (used from next raid)", 4f);
                Logger.LogInfo($"{LogTag} Repair part spot #{n} added at {add}.");
            }
            if (_clearBeaconsKey.Value.IsDown())
            {
                _beaconSpots.Value = "";
                Config.Save();
                Show("Emergency beacon spots cleared (automatic again)", 4f);
                Logger.LogInfo($"{LogTag} Emergency beacon spots cleared.");
                _emergency?.RefreshBeacons();
            }
            else if (_addBeaconKey.Value.IsDown() && !Input.GetKey(KeyCode.LeftShift))
            {
                string add = FormatVector(gw.MainPlayer.Position);
                _beaconSpots.Value = string.IsNullOrWhiteSpace(_beaconSpots.Value) ? add : _beaconSpots.Value + "; " + add;
                Config.Save();
                int n = _beaconSpots.Value.Split(';').Length;
                Show($"Emergency beacon #{n} added here (ceiling above you)", 4f);
                Logger.LogInfo($"{LogTag} Emergency beacon #{n} added at {add}.");
                _emergency?.RefreshBeacons();
            }
            MarkGateSwitch();
            BlackDivHunt.Enabled = _bdHunt.Value;
            BlackDivHunt.HostileToAll = _bdHostileAll.Value;
            BlackDivHunt.Unstick = _bdUnstick.Value;
            if (_locateBotsKey.Value.IsDown()) ShowBotDirections(gw);
            if (_bo.EventThisRaid) BlackDivHunt.Tick(gw);
            if (_bo.EventThisRaid) KillaStalk.Tick(gw);
            if (_bo.EventThisRaid) TagillaGuard.Tick(gw);
            UpdateKillaRelease();
            if (_clearPanelsKey.Value.IsDown()) PlacePanelFromView(add: true);

            if (!_announcement.Value) _bo.HideAnnouncement();
            WedgeSpawn.Tick();
            if (_bo.EventThisRaid && BlackoutApi.LightsOutAt > 0f && !_bo.GatesActivated && ExtractManager.AnyGateOpen)
                GateSquad.Trigger();                                               // opened the normal way on main power
            if (_bo.EventThisRaid && _bo.GatesActivated)
            {
                SecondWave.Trigger("gates opened with the key", atGates: false);   // the gate squad holds the gates
                GateSquad.Trigger();
            }
            SecondWave.Tick(gw.MainPlayer?.Position);
            GateSquad.Tick(gw);
            UpdateBlackDivisionRelease(gw);
            DelayCutUntilBlackDivisionIsIn();
            if (_raidSeenAt > 0f && Time.time - _raidSeenAt > 15f) GameAnnouncer.Scan(_sound.Names);
            if (!_bo.EventThisRaid) return;

            HideWhiteboard();
            _emergency?.Tick();
            if (_repairKitOn.Value) RepairKit.Tick(gw);

            ExtractManager.Surprise = _surprise.Value;

            _sound.Volume = _soundVolume.Value;
            _sound.VoiceVolume = _voiceVolume.Value;
            _sound.UsePaSpeakers = _paSpeakers.Value;
            BotNoise.Enabled = _noise.Value;
            BotNoise.RangeMultiplier = _noiseRange.Value;

            bool dark = _bo.BlackoutActive;
            if (dark && !_everCut)
            {
                LightsOutVoice();
                OnFirstCut();
            }
            if (dark) _everCut = true;

            // Keep a fresh copy of the normal ambient lighting until the first cut happens.
            if (!_everCut && Time.time >= _nextSnapshot)
            {
                _ambient.Take();
                _nextSnapshot = Time.time + 2f;
            }

            if (_everCut && _switchGo == null)
            {
                SpawnSwitch();
                if (_switchGo != null)
                {
                    _sound.VerifyAll();
                    if (!TryParseVector(_genAPos.Value, out _)) _autoGens = GeneratorFinder.Find(_switchGo.transform.position, Logger);
                }
            }

            BlackoutApi.SwitchPosition = _switchGo != null ? _switchGo.transform.position : (Vector3?)null;
            BlackoutApi.CanCut = _switchGo != null && !dark && !_burnt.Active && !SequenceRunning;
            string cutBy = BlackoutApi.TakeCutRequest();
            if (cutBy != null && !dark && !_burnt.Active && !SequenceRunning && _switchGo != null)
            {
                CutPower(cutBy);
                dark = _bo.BlackoutActive;
            }

            if (_toggleRequested && !SequenceRunning)
            {
                _toggleRequested = false;
                if (_burnt.Active) TryRepair(gw.MainPlayer);
                else if (dark) RestorePower();
                else if (StagedPartial) RestartGeneratorB();
                else if (_allowCutAgain.Value) CutPower();
                dark = _bo.BlackoutActive;
            }

            ResetSwitchIfDue(dark || StagedPartial);
            VerifySwitchReady();
            SwitchWatchdog(dark);
            if (_switch != null)
            {
                // interaction menu text (the copied gate console said "Open the gates")
                _switch.ContextMenuTip = _burnt.Active ? (_repairKitOn.Value ? "Repair generator control" : "Generator control (burnt out)")
                    : dark ? TipStart
                    : StagedPartial ? (_genBDamaged ? "Generator B (damaged)" : _faults.Remaining > 0 ? "Restart generator B (fault)" : "Restart generator B")
                    : TipStop;
            }
            UpdateIndicator(dark);
            _beacons.Tick(Time.deltaTime);
            _burnt.Tick();
            if (_burnt.Active && _falseStarts.Value && dark && !SequenceRunning && Time.time >= _nextFalseStart)
            {
                float on = UnityEngine.Random.Range(Mathf.Min(_falseStartOnMin.Value, _falseStartOnMax.Value),
                                                    Mathf.Max(_falseStartOnMin.Value, _falseStartOnMax.Value));
                // the interval is the dark time between false starts
                _nextFalseStart = Time.time + on + 60f * _falseStartMin.Value + UnityEngine.Random.Range(-6f, 6f);
                BotNoise.Emit(GenA, 120f);
                DangerFlicker.Release();       // hand the lamps back before the false start borrows them
                StartCoroutine(_stagedRestore.FalseStart(GenA, GenB, stall: _rng.Next(2) == 0, lightsSeconds: on));
            }
            _emergencyFlicker.Amount = Mathf.Max(0.25f, _flickerAmount.Value);
            _stagedRestore.FlickerAmount = _flickerAmount.Value;
            if (_lampFlicker.Value && !SequenceRunning && _flickerAmount.Value > 0f)
            {
                if (dark) _emergencyFlicker.Tick(_bo.PeekList<Light>("_emergencyLights"));
                else if (DistFault) _emergencyFlicker.Tick(_dist.EmergencyLamps);
                else if (StagedPartial) _emergencyFlicker.Tick(_bo.PeekList<Light>("_emergencyLights").FindAll(l => l != null && PowerGrid.IsGridB(l.transform.position)));
            }
            if (!dark && !SequenceRunning) _stagedRestore.AmbientFlickerTick(gw.MainPlayer.Position);

            _dist.Language = Lang;
            _dist.Voice = _voice.Value;
            _dist.Subtitles = _announcement.Value;
            var cabinets = PanelSpots();
            _dist.Tick(!dark && !Partial && !SequenceRunning, cabinets.Count > 0 ? cabinets[0] : SwitchPos);
            _faults.Tick();
            if (Partial && !SequenceRunning) _stagedRestore.KeepGridBDark();
        }

        // ---------------------------------------------------------------- restore

        private void RestorePower()
        {
            Vector3 origin = _switchGo != null ? _switchGo.transform.position : Vector3.zero;
            DangerFlicker.Release();
            _emergencyFlicker.StopAll(_bo.PeekList<Light>("_emergencyLights"));

            // Just repaired: it starts for sure - but 50/50 generator B didn't survive.
            bool repaired = _repairedReady;
            _repairedReady = false;
            if (repaired)
            {
                _genBDamaged = GenB.HasValue && _rng.Next(2) == 0;
                Logger.LogInfo($"{LogTag} First start after the repair: {(_genBDamaged ? "generator B is damaged - only generator A" : "both generators")}.");
            }

            // The control switch can short out - and every start this raid makes it more likely.
            int burn = BurnoutChance();
            _startsThisRaid++;
            if (!repaired)
            {
                Logger.LogInfo($"{LogTag} Start attempt #{_startsThisRaid}: burnout chance {burn}%.");
                if (_rng.Next(100) < burn)
                {
                    BurnOutSwitch(origin);
                    return;
                }
            }

            // Old generator: sometimes it just won't start.
            bool fail = !repaired && _failsInRow < _maxFails.Value && _rng.Next(100) < _failChance.Value;
            if (fail)
            {
                _failsInRow++;
                StartCoroutine(FailedStart(GenA));
                return;
            }
            _failsInRow = 0;

            // generator B may trip at sync -> partial power + switchgear faults
            var spots = PanelSpots();
            bool bTrip = GenB.HasValue && (_genBDamaged || (!repaired && spots.Count > 0 && _rng.Next(100) < _tripChance.Value));
            if (GenB.HasValue && spots.Count == 0 && !_panelHintShown && _tripChance.Value > 0)
            {
                _panelHintShown = true;
                Logger.LogInfo($"{LogTag} No breaker panels placed - generator B can't trip. Place cabinets in the switchgear room with {_placePanelKey.Value}.");
                Show($"Tip: place switchgear cabinets with {_placePanelKey.Value} to enable generator B faults", 7f);
            }

            if (_staged.Value && _stagedRestore != null)
            {
                var cfg = new StagedSettings
                {
                    Stages = _waves.Value,
                    GeneratorStart = _generatorStart.Value,
                    BetweenStages = _betweenWaves.Value,
                    Flicker = _flicker.Value,
                    VoiceAnnouncements = _voice.Value,
                    Subtitles = _announcement.Value,
                    Language = Lang,
                    Siren = _siren.Value,
                    GenA = GenA,
                    GenB = GenB,
                    BTrip = bTrip,
                    PartialVoice = !_genBDamaged,
                    OnBTrip = () => BotNoise.Emit(GenB ?? GenA, 200f),
                    OnPartialPower = () =>
                    {
                        _beacons.Stop();
                        if (_restoreExtracts.Value) ExtractManager.RestoreWhere(p => !PowerGrid.IsGridB(p));
                        if (_genBDamaged) Show("Generator B is damaged - only half of Labs has power", 7f);
                        else _faults.Spawn(PanelSpots(), _faultCount.Value, FindDonorSwitch());
                    },
                    OnCatch = () => BotNoise.Emit(GenA, 140f),
                    OnSirenStart = () =>
                    {
                        if (_beaconsCfg.Value) _beacons.Spawn(BeaconSpots(origin));
                    },
                    OnSirenTick = () => BotNoise.Emit(origin, 220f),
                    OnFullPower = () =>
                    {
                        _beacons.Stop();
                        if (_restoreExtracts.Value) ExtractManager.RestoreAll();
                        MaybeScheduleBreakerFailure();
                        SecondWave.Trigger("full power", atGates: false);
                    },
                };
                StartCoroutine(_stagedRestore.Run(origin, cfg, loop => { if (loop != null) _generatorLoops.Add(loop); }));
                Logger.LogInfo($"{LogTag} Generator starting - staged power restore.");
                return;
            }

            // everything at once
            _bo.RestorePower(Logger);
            _ambient.Apply();
            _sound.Play3D("breaker_near", origin, 1f, 2f, 25f);
            _generatorLoops.Add(_sound.Play3D("generator_loop", GenA, 1f, 6f, 45f, loop: true));
            if (GenB.HasValue) _generatorLoops.Add(_sound.Play3D("generator_loop", GenB.Value, 1f, 6f, 45f, loop: true));
            if (_voice.Value) _sound.PlayVoice("vo_restored_" + Lang);
            BotNoise.Emit(origin, 140f);
            if (_restoreExtracts.Value) ExtractManager.RestoreAll();
            MaybeScheduleBreakerFailure();
            if (_announcement.Value)
            {
                _bo.ShowAnnouncement("Attention, personnel! Main power restored. All facility systems are back in service.", 6f);
            }

            Logger.LogInfo($"{LogTag} Main power restored - lights back on.");
        }

        private void CutPower(string by = null)
        {
            Vector3 origin = _switchGo != null ? _switchGo.transform.position : Vector3.zero;
            _sound.Play3D("breaker_near", origin, 1f, 2f, 25f);
            if (_generatorLoops.Count > 0)
            {
                foreach (var l in _generatorLoops) _sound.FadeOut(l, 0.4f);
                _generatorLoops.Clear();
                _sound.Play3D("generator_spindown", GenA, 1f, 6f, 45f);
                if (GenB.HasValue)
                {
                    var b = GenB.Value;
                    StartCoroutine(After(0.35f, () => _sound.Play3D("generator_spindown", b, 1f, 6f, 45f)));
                }
            }

            _beacons.Remove();
            _dist.Cancel();
            _faults.Clear();
            ExtractManager.Relock(_bo, Logger);

            // Blackout's own power cut: lights out, emergency lamps, power-down sound and announcement.
            if (_bo.CutPower(Logger))
            {
                LightsOutVoice();
                Logger.LogInfo(by == null ? $"{LogTag} Power cut again from the generator room - back on emergency power."
                                          : $"{LogTag} Power cut again from the generator room - {by} shut the generators down - back on emergency power.");
            }
        }

        /// <summary>Partial power: restart generator B (only once the tripped breakers are reset).</summary>
        private void RestartGeneratorB()
        {
            if (_genBDamaged)
            {
                _sound.Play3D("cue_denied", SwitchPos, 1f, 2f, 25f);
                Show("Generator B is damaged - it won't start again this raid", 5f);
                Logger.LogInfo($"{LogTag} Generator B restart refused - damaged (after the repair).");
                return;
            }
            if (_faults.Remaining > 0)
            {
                _sound.Play3D("cue_denied", SwitchPos, 1f, 2f, 25f);      // blocked: error buzz at the panel
                if (_announcement.Value) Show($"Generator B blocked - reset the tripped breaker in the switchgear room", 5f);
                Logger.LogInfo($"{LogTag} Generator B restart refused - {_faults.Remaining} breaker fault(s) left.");
                return;
            }
            if (!GenB.HasValue) return;
            var b = GenB.Value;

            // the control can short out on a B restart too - half the current chance (and it wears it too)
            int burnB = BurnoutChance() / 2;
            _startsThisRaid++;
            Logger.LogInfo($"{LogTag} Generator B restart: burnout chance {burnB}%.");
            if (_rng.Next(100) < burnB)
            {
                Logger.LogInfo($"{LogTag} Generator B restart shorted the control ({burnB}% chance).");
                CutPower();                                         // whatever was running goes down with it
                BurnOutSwitch(_switchGo != null ? _switchGo.transform.position : SwitchPos);
                return;
            }

            // generator B is already warm: a restart fails less often, and at most once
            bool fail = _failsInRow < Math.Min(1, _maxFails.Value) && _rng.Next(100) < _failChance.Value / 2;
            if (fail)
            {
                _failsInRow++;
                StartCoroutine(FailedStart(b));
                return;
            }
            _failsInRow = 0;

            var cfg = new StagedSettings
            {
                BetweenStages = _betweenWaves.Value,
                Flicker = _flicker.Value,
                VoiceAnnouncements = _voice.Value,
                Subtitles = _announcement.Value,
                Language = Lang,
                GenA = GenA,
                GenB = b,
                OnCatch = () => BotNoise.Emit(b, 140f),
                OnFullPower = () =>
                {
                    _faults.Clear();
                    if (_restoreExtracts.Value) ExtractManager.RestoreAll();
                    MaybeScheduleBreakerFailure();
                    SecondWave.Trigger("full power", atGates: false);
                },
            };
            StartCoroutine(_stagedRestore.RunRestartB(cfg, loop => { if (loop != null) _generatorLoops.Add(loop); }));
            Logger.LogInfo($"{LogTag} Generator B restarting.");
        }

        /// <summary>Burnt control + the parts in your inventory: ~12 s of work next to the switch.</summary>
        private void TryRepair(Player me)
        {
            if (!_repairKitOn.Value || me == null)
            {
                _burnt.Zap();
                return;
            }
            if (!RepairKit.HasParts(me, out var missing))
            {
                _burnt.Zap();
                Show($"Generator control burnt out. To repair it you need: {missing}", 6f);
                Logger.LogInfo($"{LogTag} Repair refused - missing: {missing}.");
                return;
            }
            StartCoroutine(Repair(me));
        }

        private IEnumerator Repair(Player me)
        {
            _repairRunning = true;
            var at = SwitchPos;
            Logger.LogInfo($"{LogTag} Repairing the generator control...");
            bool done = false;
            _burnt.Quiet(true);
            try
            {
                const float duration = 12f;
                float t0 = Time.time;
                while (Time.time - t0 < duration)
                {
                    if (me == null || me.HealthController == null || !me.HealthController.IsAlive || (me.Position - at).sqrMagnitude > 3.5f * 3.5f)
                    {
                        Show("Repair interrupted - stay at the generator control", 4f);
                        Logger.LogInfo($"{LogTag} Repair interrupted (moved away).");
                        yield break;
                    }
                    Show($"Repairing the generator control... {Mathf.CeilToInt(duration - (Time.time - t0))} s (stay close)", 1.2f);
                    if (_rng.Next(3) == 0) _sound.Play3D(_rng.Next(2) == 0 ? "spark1" : "spark2", at, 0.6f, 2f, 15f);
                    else _sound.Play3D("breaker_near", at, 0.35f, 2f, 15f);
                    yield return new WaitForSeconds(1f + (float)_rng.NextDouble() * 0.6f);
                }
                if (!RepairKit.HasParts(me, out var missing) || !RepairKit.UseParts(me))
                {
                    Show($"Repair failed - missing: {missing}", 5f);
                    yield break;
                }
                done = true;
                _burnt.Clear();
                _beacons.Remove();
                _repairedReady = true;
                _startsThisRaid = 0;          // new parts: the wear starts over
                _sound.Play3D("cue_ready", at, 1f, 2f, 25f);
                if (_voice.Value) _sound.PlayVoice("vo_control_repaired_" + Lang);
                Show("Generator control repaired - start the generator", 6f);
                Logger.LogInfo($"{LogTag} Generator control REPAIRED - the next start is certain (50/50 one or both generators).");
            }
            finally
            {
                _repairRunning = false;
                _burnt.Quiet(false);
                if (!done) _switchResetAt = Time.time + 0.5f;
            }
        }

        /// <summary>No power, nothing works: Blackout's code whiteboard goes too.</summary>
        private void HideWhiteboard()
        {
            if (_emergencyCode.Value || _whiteboardHidden || Time.time < _nextWhiteboardCheck) return;
            _nextWhiteboardCheck = Time.time + 2f;
            var wb = GameObject.Find("blackout_whiteboard");
            if (wb == null) return;
            wb.SetActive(false);
            _whiteboardHidden = true;
            Logger.LogInfo($"{LogTag} Emergency code off: whiteboard removed.");
        }

        private int BurnoutChance() => Math.Min(90, _burnoutChance.Value + _burnoutStep.Value * _startsThisRaid);

        /// <summary>The generator control switch shorts out: no main power for the rest of the raid.</summary>
        private void BurnOutSwitch(Vector3 origin)
        {
            _burnt.Burn(origin);
            BotNoise.Emit(origin, 120f);
            _beacons.Spawn(new List<Vector3> { origin + Vector3.up * 1.6f, GenA + Vector3.up * 1.8f });   // red beacons keep turning down there
            _nextFalseStart = Time.time + 60f * _falseStartMin.Value;
            if (_voice.Value) StartCoroutine(After(1.5f, () => _sound.PlayVoice("vo_control_failure_" + Lang)));
            if (_announcement.Value)
            {
                StartCoroutine(After(2.5f, () => _bo.ShowAnnouncement(
                    "Attention, personnel! Main generator control failure. Due to technical problems, the facility will remain without main power.", 9f)));
            }
            Logger.LogInfo($"{LogTag} GENERATOR CONTROL BURNT OUT - no main power for the rest of the raid.");
        }

        private IEnumerator FailedStart(Vector3 origin)
        {
            _failedStartRunning = true;
            try
            {
                string clip = _rng.Next(2) == 0 ? "generator_fail1" : "generator_fail2";
                _sound.Play3D(clip, origin, 1f, 6f, 45f);
                Logger.LogInfo($"{LogTag} Generator failed to start ({_failsInRow}/{_maxFails.Value} in a row) - try again.");

                yield return new WaitForSeconds(1.0f);
                BotNoise.Emit(origin, 70f);                      // starter cranking
                if (clip == "generator_fail2")
                {
                    yield return new WaitForSeconds(2.2f);
                    BotNoise.Emit(origin, 160f);                 // backfire
                }

                float rest = Mathf.Max(0.5f, _sound.Length(clip) - (clip == "generator_fail2" ? 3.2f : 1.0f)) + 0.4f;
                yield return new WaitForSeconds(rest);

                if (_voice.Value) _sound.PlayVoice("vo_fail_" + Lang);
                if (_announcement.Value)
                {
                    yield return new WaitForSeconds(0.8f);
                    _bo.ShowAnnouncement("Attention, personnel! Generator start failure. Please stand by.", 4.5f);
                }
                yield return new WaitForSeconds(2.0f);
            }
            finally
            {
                _failedStartRunning = false;
            }
        }

        private static IEnumerator After(float seconds, Action a)
        {
            yield return new WaitForSeconds(seconds);
            a();
        }

        /// <summary>Corridor emergency-lamp spots + the generator room itself.</summary>
        private System.Collections.Generic.List<Vector3> BeaconSpots(Vector3 origin)
        {
            var spots = _bo.EmergencyLampPositions();
            spots.Add(origin + Vector3.up * 1.6f);
            return spots;
        }

        private void OnSwitchStateChanged(WorldInteractiveObject obj, EDoorState prev, EDoorState next)
        {
            Logger.LogInfo($"{LogTag} Switch {prev} -> {next}{(SequenceRunning ? " (sequence running, ignored)" : "")}");
            if (next == EDoorState.Shut && _selfClosing)
            {
                _selfClosing = false;              // our own reset finished - not a player flip
                return;
            }
            // A player flip normally ends "Open". If the switch got out of step it can end "Shut" instead
            // (Interacting -> Shut) - that is still the player using it, so it counts too.
            bool playerFlip = next == EDoorState.Open || (next == EDoorState.Shut && prev == EDoorState.Interacting);
            if (SequenceRunning && next == EDoorState.Open)
            {
                _switchCheckAt = Time.time + 2f;   // flipped during a sequence: put it back to "off" once it's over
                return;
            }
            if (!playerFlip || Time.time < _nextToggleAllowed || SequenceRunning) return;
            _toggleRequested = true;
            _switchCheckAt = 0f;                   // a pending safety check must not undo this flip
            _nextToggleAllowed = Time.time + 1f;   // no double-flips (shorter than the reset below)
            _switchResetAt = next == EDoorState.Open ? Time.time + 1.5f : 0f;   // let the animation finish, then flip it back
        }

        /// <summary>Puts the switch back to "off" so it can be used again.</summary>
        private void ResetSwitchIfDue(bool dark)
        {
            if (_switch == null || _switchResetAt <= 0f || Time.time < _switchResetAt || SequenceRunning) return;
            if ((_switch.DoorState & EDoorState.Interacting) != 0) return;   // still animating

            // Power is on and cutting it again is disabled: leave it switched on.
            if (!dark && !_allowCutAgain.Value)
            {
                _switchResetAt = 0f;
                return;
            }

            if ((_switch.DoorState & EDoorState.Open) != 0)
            {
                // The game's own way of flipping a switch back (same as chained switches use).
                try
                {
                    _selfClosing = true;
                    _switch.LockForInteraction();
                    _switch.Interact(new InteractionResult(EInteractionType.Close));
                }
                catch (Exception ex)
                {
                    _selfClosing = false;
                    Logger.LogWarning($"{LogTag} switch close interaction failed ({ex.Message}), forcing it.");
                    _switch.DoorState = EDoorState.Shut;
                }
                _switchCheckAt = Time.time + 2.5f;
            }
            _switchResetAt = 0f;
        }

        /// <summary>
        /// Last safety net: a switch left "on" has no menu option at all. If it sits there while it should be usable
        /// (dark, partial power, or cutting allowed), flip it back.
        /// </summary>
        private void SwitchWatchdog(bool dark)
        {
            if (_switch == null || SequenceRunning || Time.time < _nextWatchdog) return;
            _nextWatchdog = Time.time + 3f;
            if (_switch.DoorState != EDoorState.Open || _switchResetAt > 0f || _switchCheckAt > 0f) return;
            if (!(dark || StagedPartial || _burnt.Active || _allowCutAgain.Value)) return;
            Logger.LogInfo($"{LogTag} Switch was left on with no option - flipping it back.");
            _switchResetAt = Time.time;
        }

        private float _nextWatchdog;

        /// <summary>Safety net: if the close animation didn't land on "off", force it.</summary>
        private void VerifySwitchReady()
        {
            if (_switch == null || _switchCheckAt <= 0f || Time.time < _switchCheckAt) return;
            _switchCheckAt = 0f;
            if (_switch.DoorState == EDoorState.Open)
            {
                // left on (e.g. flipped during a sequence): flip it back properly, never by force
                if (!SequenceRunning) _switchResetAt = Time.time;
                else _switchCheckAt = Time.time + 2f;
                return;
            }
            if (_switch.DoorState != EDoorState.Shut)
            {
                Logger.LogWarning($"{LogTag} switch stuck in state {_switch.DoorState} - forcing it to off.");
                _selfClosing = false;
                _switch.DoorState = EDoorState.Shut;
            }
            _switch.Operatable = true;
            Logger.LogInfo($"{LogTag} Switch ready again (state {_switch.DoorState}, power {(_bo.BlackoutActive ? "OFF" : "ON")}).");
        }

        // ---------------------------------------------------------------- switch

        private void SpawnSwitch()
        {
            if (!TryGetSwitchPose(out Vector3 pos, out Quaternion rot, out string source))
            {
                if (!_missingPositionWarned)
                {
                    _missingPositionWarned = true;
                    Logger.LogWarning($"{LogTag} No switch position: '{AutoPanelName}' not found and none saved. " +
                                      $"Look at the generator room panel and press {_placeKey.Value}.");
                    Show($"Power switch has no position yet - look at the panel and press {_placeKey.Value}", 8f);
                }
                return;
            }

            Switch donor = FindDonorSwitch();
            if (donor == null)
            {
                return; // exfils not ready yet, try again next frame
            }

            _switchGo = Instantiate(donor.gameObject);
            _switchGo.name = SwitchName;
            _switchGo.transform.SetPositionAndRotation(pos, rot);

            Switch sw = _switchGo.GetComponent<Switch>();
            _switch = sw;
            sw.Id = SwitchName;
            sw.ExfiltrationPoint = null;
            sw.Door = null;
            sw.NextSwitches = Array.Empty<Switch.SwitchAndOperation>();
            sw.PreviousSwitch = null;
            sw.AutoTurnOff = false;
            sw.DoorState = EDoorState.Shut;
            sw.Operatable = true;
            sw.OnDoorStateChanged += OnSwitchStateChanged;
            sw.ExtractionZoneTip = string.Empty;
            sw.ContextMenuTip = TipStart;
            _switchGo.SetActive(true);

            if (_indicatorLight.Value)
            {
                var lightGo = new GameObject(SwitchName + "_indicator");
                lightGo.transform.SetParent(_switchGo.transform, false);
                lightGo.transform.localPosition = new Vector3(0f, 0.15f, 0.1f);
                _indicator = lightGo.AddComponent<Light>();
                _indicator.type = LightType.Point;
                _indicator.color = new Color(1f, 0.08f, 0.05f);
                _indicator.range = 2.5f;
                _indicator.intensity = 0.6f;
                _indicator.shadows = LightShadows.None;
            }

            var col = _switchGo.GetComponentInChildren<Collider>(true);
            Logger.LogInfo($"{LogTag} Power switch spawned at {pos} ({source}), collider {(col != null ? col.name : "MISSING")}.");
        }

        private static Switch FindDonorSwitch()
        {
            var points = ExfiltrationController.Instance?.ExfiltrationPoints;
            if (points == null) return null;
            foreach (var p in points)
            {
                if (p != null && p.Settings != null && p.Settings.Name == DonorExfil && p.Switch != null)
                {
                    return p.Switch;
                }
            }
            return null;
        }

        private bool TryGetSwitchPose(out Vector3 pos, out Quaternion rot, out string source)
        {
            if (TryParseVector(_switchPosition.Value, out pos))
            {
                rot = TryParseVector(_switchRotation.Value, out Vector3 euler) ? Quaternion.Euler(euler) : Quaternion.identity;
                source = "saved position";
                return true;
            }

            // Automatic: put it on the existing boiler/generator control panel.
            rot = Quaternion.identity;
            source = AutoPanelName;
            GameObject panel = GameObject.Find(AutoPanelName);
            if (panel == null) return false;

            var renderers = panel.GetComponentsInChildren<Renderer>(true)
                .Where(r => r != null && !r.gameObject.name.Contains("SHADOW")).ToArray();
            Vector3 center = panel.transform.position;
            if (renderers.Length > 0)
            {
                Bounds b = renderers[0].bounds;
                foreach (var r in renderers) b.Encapsulate(r.bounds);
                center = b.center;
            }

            // Pick the side of the panel that faces open air (the others go into the wall).
            Transform t = panel.transform;
            Vector3[] dirs = { t.forward, -t.forward, t.right, -t.right };
            Vector3 best = t.forward;
            float bestFree = -1f;
            foreach (var d in dirs)
            {
                float free = Physics.Raycast(center + d * 0.05f, d, out RaycastHit hit, 1.5f, ~0, QueryTriggerInteraction.Ignore)
                    ? hit.distance
                    : 1.5f;
                if (free > bestFree)
                {
                    bestFree = free;
                    best = d;
                }
            }

            pos = center + best * 0.12f;
            rot = Quaternion.LookRotation(best, Vector3.up);
            return true;
        }

        // ---------------------------------------------------------------- placement

        private Vector3 SwitchPos => _switchGo != null ? _switchGo.transform.position : Vector3.zero;
        private bool StagedPartial => _stagedRestore != null && _stagedRestore.IsPartial;     // generator B tripped at sync
        private bool DistFault => _dist != null && _dist.Active;                              // breaker failure later on
        private bool Partial => StagedPartial || DistFault;                                    // sector B has no power

        private float _raidSeenAt = -1f, _cutSeenAt = -1f;

        /// <summary>Black Division is held back at raid start and moves in a few seconds after the power cuts.</summary>
        private void UpdateBlackDivisionRelease(GameWorld gw)
        {
            if (_raidSeenAt < 0f) _raidSeenAt = Time.time;
            if (!WedgeSpawn.Holding) return;

            // no blackout event on this client (shouldn't happen) - don't keep them forever
            if (!_bo.EventThisRaid && Time.time - _raidSeenAt > 30f)
            {
                WedgeSpawn.Release(useRoom: true);
                return;
            }
            if (!_bo.BlackoutActive && !_everCut) return;
            if (_cutSeenAt < 0f) _cutSeenAt = Time.time;
            if (Time.time - _cutSeenAt < 5f) return;     // lights go out... then they come in

            // if you're near the generator room the spawn hook sends them somewhere well away from you
            WedgeSpawn.Release(useRoom: true);
            PlayGameLine(_gameIntruders.Value, 4f);
        }

        /// <summary>
        /// "Before the blackout" mode: Black Division is the reason the lights go out. Blackout's countdown is held
        /// until the whole group is in, then the cut follows a few seconds later (max 90 s wait).
        /// </summary>
        private void DelayCutUntilBlackDivisionIsIn()
        {
            if (_bdTiming.Value != BlackDivisionTiming.BeforeTheBlackout || !_bdGenRoom.Value) return;
            if (!_bo.EventThisRaid || _bo.BlackoutActive || _everCut || !_bo.ClockStarted) return;
            if (WedgeSpawn.WaveSeenAt < 0f || Time.time - WedgeSpawn.WaveSeenAt > 150f) return;

            // the guards are generated after Wedge and can take a minute: the lights go out once they're all in
            int needed = Mathf.Max(1, WedgeSpawn.Expected);
            bool readyEnough = BlackDivHunt.TotalSeen >= needed;
            if (!readyEnough && Time.time >= _nextHoldLog)
            {
                _nextHoldLog = Time.time + 10f;
                Logger.LogInfo($"{LogTag} Holding the blackout: Black Division {BlackDivHunt.TotalSeen}/{needed} in.");
            }
            if (readyEnough && _bdReadyAt < 0f)
            {
                _bdReadyAt = Time.time;
                Logger.LogInfo($"{LogTag} Black Division is in the generator room ({BlackDivHunt.TotalSeen}/{WedgeSpawn.Expected}) - the power goes out shortly.");
            }
            float earliest = readyEnough ? _bdReadyAt + 3f : Time.time + 2f;
            if (_bo.BlackoutAt < earliest) _bo.BlackoutAt = earliest;
        }

        /// <summary>The first blackout of the raid: if Black Division did it, you hear the generators being shut down.</summary>
        private void OnFirstCut()
        {
            if (_bdTiming.Value != BlackDivisionTiming.BeforeTheBlackout || WedgeSpawn.WaveSeenAt < 0f) return;
            _sound.Play3D("breaker_trip", GenA, 1f, 3f, 60f);
            _sound.Play3D("generator_spindown", GenA, 1f, 6f, 45f);
            if (GenB.HasValue)
            {
                var b = GenB.Value;
                StartCoroutine(After(0.6f, () => _sound.Play3D("generator_spindown", b, 1f, 6f, 45f)));
            }
            BotNoise.Emit(GenA, 120f);
            PlayGameLine(_gameIntruders.Value, 4f);
            Logger.LogInfo($"{LogTag} Black Division shut the generators down - blackout.");
        }

        /// <summary>One of the game's own announcer lines, after a delay (skipped if the name is empty).</summary>
        private void PlayGameLine(string spec, float delay)
        {
            if (string.IsNullOrWhiteSpace(spec) || !_voice.Value) return;
            StartCoroutine(After(delay, () => GameAnnouncer.Speak(_sound, spec)));
        }

        /// <summary>Any power cut: the game's own "lights out" announcement instead of Blackout's.</summary>
        private void LightsOutVoice()
        {
            if (string.IsNullOrWhiteSpace(_gameLightsOut.Value) || !_voice.Value) return;
            _bo.SuppressCutAnnouncement();
            PlayGameLine(_gameLightsOut.Value, 1.5f);
        }

        private Light _gateLamp;

        /// <summary>Blackout's gate switch gets a small blinking green lamp so you can spot it in the dark.</summary>
        private void MarkGateSwitch()
        {
            if (_gateLamp != null)
            {
                _gateLamp.intensity = Mathf.Repeat(Time.time, 1.6f) < 0.25f ? 0.7f : 0.05f;
                return;
            }
            if (Time.frameCount % 120 != 0) return;
            var go = GameObject.Find("blackout_admin_switch");
            if (go == null) return;
            var l = new GameObject("PowerRestore_gate_lamp");
            l.transform.SetParent(go.transform, false);
            l.transform.localPosition = new Vector3(0f, 0.2f, 0.1f);
            _gateLamp = l.AddComponent<Light>();
            _gateLamp.type = LightType.Point;
            _gateLamp.color = new Color(0.2f, 1f, 0.35f);
            _gateLamp.range = 2.5f;
            _gateLamp.shadows = LightShadows.None;
        }

        private void ShowGateDirections(Vector3 me, Vector3 facing)
        {
            string Describe(string name, string label)
            {
                var go = GameObject.Find(name);
                if (go == null) return label + ": not in this raid";
                var d = go.transform.position - me;
                var flat = new Vector3(d.x, 0f, d.z);
                float ang = Vector3.SignedAngle(new Vector3(facing.x, 0f, facing.z), flat, Vector3.up);
                string side = Mathf.Abs(ang) < 30f ? "ahead" : Mathf.Abs(ang) > 150f ? "behind you" : ang > 0 ? "to your right" : "to your left";
                string floor = Mathf.Abs(d.y) < 2.5f ? "same floor" : d.y > 0 ? "a floor up" : "a floor down";
                return $"{label}: {flat.magnitude:0} m {side}, {floor}";
            }
            Show(Describe("blackout_admin_switch", "Gate switch") + "  |  " + Describe("blackout_whiteboard", "Code board"), 8f);
        }

        // kept in its own method: if BigBrain is missing, only this call fails (not the whole plugin)
        private void InstallHuntLayer()
        {
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("xyz.drakia.bigbrain"))
                {
                    Logger.LogWarning($"{LogTag} BigBrain not found - Black Division only gets hunt orders.");
                    return;
                }
                InstallHuntLayerInner();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"{LogTag} hunt layer setup failed: {ex.Message}");
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        // above the game's "AssaultHaveEnemy" (50: knows an enemy, waits) but below Pursuit (55), cover/fight (56-80):
        // the hunt layer only runs while they see nobody, so any real fight still goes to the combat layers
        private void InstallHuntLayerInner() => HuntLayerSetup.Install(Logger, 52);

        private void InstallGateGuardLayer()
        {
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("xyz.drakia.bigbrain"))
                {
                    Logger.LogWarning($"{LogTag} BigBrain not found - the gate squad will hunt instead of holding the gates.");
                    return;
                }
                InstallGateGuardLayerInner();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"{LogTag} gate guard layer setup failed: {ex.Message}");
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        // just above the hunt layer (52): while nobody's in sight a gate soldier goes back to his gate instead of hunting
        private void InstallGateGuardLayerInner() => GateGuardSetup.Install(Logger, 53);

        private void InstallTagillaLayer()
        {
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("xyz.drakia.bigbrain"))
                {
                    Logger.LogWarning($"{LogTag} BigBrain not found - Tagilla won't hold the sewage exit.");
                    return;
                }
                InstallTagillaLayerInner();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"{LogTag} Tagilla guard layer setup failed: {ex.Message}");
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        // Tagilla's brain: AvoidDanger 80, Malfunction 78, FightRequest 70, Ambush 60, Main 50 ... - the guard layer
        // sits above his ambush/main layers; it steps aside by itself while he fights someone near the exit
        // 79: also above Shadow of Tagilla's own "Khorovod" layer (77), still below AvoidDanger / SAIN Avoid Threat (80)
        private void InstallTagillaLayerInner() => TagillaGuardSetup.Install(Logger, 79);

        /// <summary>Lamps near a hunting Tagilla (after Blackout and the normal lamp flicker each frame).</summary>
        private void LateUpdate()
        {
            try { PresenceLights.Tick(_sound); } catch (Exception) { }      // The Stalker: the lights around him die
            try
            {
                if (_bo == null || !_bo.Ok || !_bo.EventThisRaid || Singleton<GameWorld>.Instance?.MainPlayer == null)
                {
                    DangerFlicker.Release();
                    return;
                }
                if (SequenceRunning) return;              // released when the sequence started
                IList<Light> lamps = null;
                if (_bo.BlackoutActive) lamps = _bo.PeekList<Light>("_emergencyLights");
                else if (DistFault) lamps = _dist.EmergencyLamps;
                else if (StagedPartial) lamps = _bo.PeekList<Light>("_emergencyLights").FindAll(l => l != null && PowerGrid.IsGridB(l.transform.position));
                DangerFlicker.Apply(lamps, _sound);
            }
            catch (Exception)
            {
                // never break the frame for a light effect
            }
        }

        /// <summary>Surprise Killa: 30-60 s after the last Black Division dies (or at the backup time).</summary>
        private void UpdateKillaRelease()
        {
            if (!WedgeSpawn.KillaHeld || _raidSeenAt < 0f) return;
            if (_killaReleaseAt < 0f)
            {
                // only Wedge's team and our second wave count - Black Division's own extra gate squads don't
                // a soldier frozen for 2+ min (SAIN stuck on a gas grenade) doesn't hold the raid up
                bool wiped = BlackDivHunt.OurSeen > 0 && BlackDivHunt.OurActive == 0 && !SecondWave.Pending && !WedgeSpawn.Holding;
                if (wiped)
                {
                    _killaReleaseAt = Time.time + 30f + (float)_rng.NextDouble() * 30f;
                    int frozen = BlackDivHunt.OurFrozen;
                    Logger.LogInfo(frozen > 0
                        ? $"{LogTag} Black Division out of the fight ({BlackDivHunt.OurSeen - frozen} killed, {frozen} frozen for 2+ min) - Killa breaks in in ~{_killaReleaseAt - Time.time:0} s."
                        : $"{LogTag} Black Division wiped out ({BlackDivHunt.OurSeen} killed) - Killa breaks in in ~{_killaReleaseAt - Time.time:0} s.");
                }
                else if (Time.time - _raidSeenAt >= _killaBackup.Value * 60f)
                {
                    WedgeSpawn.ReleaseKilla($"backup time {_killaBackup.Value:0} min, Black Division still alive");
                }
                return;
            }
            if (BlackDivHunt.OurActive > 0 || SecondWave.Pending)
            {
                // more Black Division turned up (second wave) - Killa waits for them to die too
                _killaReleaseAt = -1f;
                Logger.LogInfo($"{LogTag} More Black Division arrived - Killa waits until they are dead too.");
                return;
            }
            if (Time.time >= _killaReleaseAt) WedgeSpawn.ReleaseKilla("Black Division wiped out");
        }

        /// <summary>The hatch gives way with a bang heard across the basement, then he screams.</summary>
        private void KillaEntrance(Player killa)
        {
            var pos = killa.Position;
            if (_conduitWithKilla.Value) ExtractManager.Open("lab_Under_Storage_Collector", permanent: true);
            _sound.Play3D("killa_hatch", pos, 0.9f, 6f, 260f);                // carries across the basement: clear to ~60 m, faint to ~150 m
            StartCoroutine(After(8f, LogKillaVoiceClips));
            StartCoroutine(After(3.0f, () => BotNoise.Emit(pos, 70f)));       // bots close by hear it too
            // his own voice clips, played by us so they echo through the basement (his normal voice dies out at ~40 m)
            void Scream(float at, string clip, EPhraseTrigger fallback) => StartCoroutine(After(at, () =>
            {
                try
                {
                    if (killa == null || killa.HealthController == null || !killa.HealthController.IsAlive) return;
                    bool echo = GameAnnouncer.Play3D(_sound, clip, killa.Position + Vector3.up * 1.6f, 1f, 6f, 200f);
                    if (!echo) killa.Say(fallback, true, 0f, ETagStatus.Combat | ETagStatus.Aware, 100, true);
                    var me = Singleton<GameWorld>.Instance?.MainPlayer;
                    Logger.LogInfo($"{LogTag} Killa screams ({(echo ? clip : fallback.ToString())}){(me != null ? $", {(me.Position - killa.Position).magnitude:0} m from you" : "")}.");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"{LogTag} Killa scream failed: {ex.Message}");
                }
            }));
            Scream(6.6f, "killa_fight_25_l", EPhraseTrigger.OnFight);          // right after the last bang
            Scream(11.4f, "killa_agony_04", EPhraseTrigger.OnBeingHurt);
            Scream(16.4f, "killa_fight_24_l", EPhraseTrigger.OnFight);
            Scream(20.4f, "killa_fight_01", EPhraseTrigger.OnEnemyConversation);
            // the facility notices: PA once the screaming is over
            StartCoroutine(After(25.5f, () =>
            {
                if (_voice.Value) _sound.PlayVoice("vo_conduit_breach_" + Lang);
                if (_announcement.Value) _bo.ShowAnnouncement("Attention, security! Breach in the sewage conduit. Intruders on the lower level.", 6f);
            }));
        }

        /// <summary>Testing: which of Killa's voice clips the game has loaded (to play his screams further later).</summary>
        private void LogKillaVoiceClips()
        {
            try
            {
                var names = new SortedSet<string>();
                foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
                {
                    if (c != null && c.name.IndexOf("killa", StringComparison.OrdinalIgnoreCase) >= 0) names.Add($"{c.name} {c.length:0.0}s");
                }
                Logger.LogInfo($"{LogTag} Killa voice clips loaded ({names.Count}): {string.Join(", ", names.Take(60))}.");
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"{LogTag} Killa voice clip scan failed: {ex.Message}");
            }
        }

        /// <summary>Tagilla follows Killa through the hatch. Once Killa's screaming is over, you hear him too.</summary>
        private void TagillaEntrance(Player tagilla)
        {
            StartCoroutine(After(12f, () =>
            {
                try
                {
                    if (tagilla != null && tagilla.HealthController != null && tagilla.HealthController.IsAlive)
                        tagilla.Say(EPhraseTrigger.OnFight, true, 0f, ETagStatus.Combat | ETagStatus.Aware, 100, true);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"{LogTag} Tagilla shout failed: {ex.Message}");
                }
            }));
        }

        private void ShowBotDirections(GameWorld gw)
        {
            var me = gw.MainPlayer;
            if (me == null) return;
            string Where(Vector3 pos, string label)
            {
                var d = pos - me.Position;
                var flat = new Vector3(d.x, 0f, d.z);
                float ang = Vector3.SignedAngle(new Vector3(me.Transform.forward.x, 0f, me.Transform.forward.z), flat, Vector3.up);
                string side = Mathf.Abs(ang) < 30f ? "ahead" : Mathf.Abs(ang) > 150f ? "behind you" : ang > 0 ? "to your right" : "to your left";
                string floor = Mathf.Abs(d.y) < 2.5f ? "same floor" : d.y > 0 ? "a floor up" : "a floor down";
                return $"{label}: {flat.magnitude:0} m {side}, {floor}";
            }
            string killa = KillaStalk.Where.HasValue ? Where(KillaStalk.Where.Value, KillaStalk.Name + " (" + KillaStalk.State + ")") : KillaStalk.Name + ": " + KillaStalk.State;
            Player bd = null;
            float best = float.MaxValue;
            foreach (var p in gw.AllAlivePlayersList)
            {
                string role = p?.Profile?.Info?.Settings?.Role.ToString() ?? "";
                if (p == null || !p.IsAI || (role.IndexOf("blackdiv", StringComparison.OrdinalIgnoreCase) < 0 &&
                                             role.IndexOf("wedge", StringComparison.OrdinalIgnoreCase) < 0)) continue;
                float dd = (p.Position - me.Position).sqrMagnitude;
                if (dd < best) { best = dd; bd = p; }
            }
            string bdText = bd != null ? Where(bd.Position, "Black Division (" + bd.Profile?.Nickname + ")") : "Black Division: none alive";
            var part = RepairKit.NearestPart(me.Position);
            string partText = (part.HasValue ? Where(part.Value.pos, part.Value.name) : "Parts: none on the floor") +
                              (RepairKit.CarrierName(gw) != null ? $"; spark plug: {RepairKit.CarrierName(gw)}" : "");
            string em = _emergency != null && _emergency.Active ? $"  |  Emergency power: {_emergency.Remaining:0} s" : "";
            string tag = TagillaGuard.Where.HasValue ? Where(TagillaGuard.Where.Value, TagillaGuard.Name + " (" + TagillaGuard.State + ")") : TagillaGuard.Name + ": " + TagillaGuard.State;
            Show(killa + "  |  " + tag + "  |  " + bdText + "  |  " + partText + em, 10f);
            Logger.LogInfo($"{LogTag} {killa} | {bdText} | {partText}{em}");
        }

        private void MaybeScheduleBreakerFailure()
        {
            if (_dist == null || PanelSpots().Count == 0) return;
            if (_rng.Next(100) >= _breakerFailChance.Value) return;
            float min = Mathf.Min(_breakerFailMin.Value, _breakerFailMax.Value);
            float max = Mathf.Max(_breakerFailMin.Value, _breakerFailMax.Value);
            _dist.Schedule(min, max);
        }

        // placed by hand (config) > found in the scene > the switch itself
        private Vector3 GenA => TryParseVector(_genAPos.Value, out var a) ? a
            : (_autoGens != null && _autoGens.Count > 0 ? _autoGens[0] : SwitchPos);

        private Vector3? GenB => TryParseVector(_genBPos.Value, out var b) ? b
            : (!TryParseVector(_genAPos.Value, out _) && _autoGens != null && _autoGens.Count > 1 ? _autoGens[1] : (Vector3?)null);

        private List<Vector3> PanelSpots()
        {
            var list = new List<Vector3>();
            foreach (var part in (_panelPositions.Value ?? "").Split(';'))
            {
                if (TryParseVector(part, out var v)) list.Add(v);
            }
            return list;
        }

        private void PlacePanelFromView(bool add)
        {
            if (!TryLookAt(out RaycastHit hit)) return;
            var spot = hit.point + hit.normal * 0.1f;
            var spots = add ? PanelSpots() : new List<Vector3>();
            if (spots.Any(p => (p - spot).sqrMagnitude < 0.5f * 0.5f))
            {
                Show("That cabinet is already saved", 3f);
                return;
            }
            spots.Add(spot);
            _panelPositions.Value = string.Join("; ", spots.ConvertAll(FormatVector));
            Config.Save();
            Logger.LogInfo($"{LogTag} Breaker cabinet {(add ? "#" + spots.Count + " added" : "set")}: {FormatVector(spot)} on '{hit.collider.name}'.");
            Show(add ? $"Extra breaker cabinet #{spots.Count} added" : "Breaker cabinet set", 4f);
        }

        private void PlaceGeneratorFromView()
        {
            if (!TryLookAt(out RaycastHit hit)) return;
            Vector3 pos = hit.point + hit.normal * 0.3f;   // just in front of the engine
            bool isA = _nextGenSlot == 0;
            (isA ? _genAPos : _genBPos).Value = FormatVector(pos);
            Config.Save();
            _nextGenSlot = isA ? 1 : 0;
            Logger.LogInfo($"{LogTag} Generator {(isA ? "A" : "B")} position saved: {FormatVector(pos)} on '{hit.collider.name}'.");
            Show($"Generator {(isA ? "A" : "B")} position saved{(isA ? " - now look at the other one and press again" : "")}", 5f);
        }

        private bool TryLookAt(out RaycastHit hit)
        {
            hit = default;
            Camera cam = Camera.main ?? Camera.allCameras.FirstOrDefault(c => c != null && c.enabled);
            if (cam == null)
            {
                Show("Could not find the player camera", 4f);
                return false;
            }
            var hits = Physics.RaycastAll(new Ray(cam.transform.position, cam.transform.forward), 6f, ~0, QueryTriggerInteraction.Ignore)
                .Where(h => h.distance > 0.35f && (_switchGo == null || !h.collider.transform.IsChildOf(_switchGo.transform)))
                .OrderBy(h => h.distance).ToArray();
            if (hits.Length == 0)
            {
                Show("Look at it from closer (within 6 m) and try again", 4f);
                return false;
            }
            hit = hits[0];
            return true;
        }

        private void PlaceSwitchFromView(GameWorld gw)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                cam = Camera.allCameras.FirstOrDefault(c => c != null && c.enabled && c.name.IndexOf("FPS", StringComparison.OrdinalIgnoreCase) >= 0)
                      ?? Camera.allCameras.FirstOrDefault(c => c != null && c.enabled);
            }
            if (cam == null)
            {
                Show("Could not find the player camera", 4f);
                return;
            }

            var ray = new Ray(cam.transform.position, cam.transform.forward);
            var hits = Physics.RaycastAll(ray, 4f, ~0, QueryTriggerInteraction.Ignore)
                .Where(h => h.distance > 0.35f && (_switchGo == null || !h.collider.transform.IsChildOf(_switchGo.transform)))
                .OrderBy(h => h.distance)
                .ToArray();

            if (hits.Length == 0)
            {
                Show("Look at a wall or panel (within 4 m) and try again", 4f);
                return;
            }

            RaycastHit hit = hits[0];
            Vector3 pos = hit.point + hit.normal * 0.06f;
            Quaternion rot = Quaternion.LookRotation(hit.normal, Vector3.up);

            _switchPosition.Value = FormatVector(pos);
            _switchRotation.Value = FormatVector(rot.eulerAngles);
            Config.Save();

            if (_switchGo != null)
            {
                _switchGo.transform.SetPositionAndRotation(pos, rot);
            }
            _missingPositionWarned = false;

            Logger.LogInfo($"{LogTag} Switch position saved: {_switchPosition.Value} (rotation {_switchRotation.Value}) on '{hit.collider.name}'.");
            Show("Power switch position saved", 4f);
        }

        // ---------------------------------------------------------------- misc

        private void UpdateIndicator(bool dark)
        {
            if (_indicator == null) return;

            if (SequenceRunning)
            {
                // Generator starting: fast amber blink.
                _indicator.color = new Color(1f, 0.6f, 0.05f);
                _indicator.intensity = Mathf.PingPong(Time.time * 3f, 0.6f) + 0.1f;
            }
            else if (Partial)
            {
                // Partial power: slow amber pulse until generator B is back.
                _indicator.color = new Color(1f, 0.6f, 0.05f);
                _indicator.intensity = Mathf.PingPong(Time.time * 0.6f, 0.5f) + 0.15f;
            }
            else if (_burnt.Active)
            {
                // dead: a faint red glow that stutters
                _indicator.color = new Color(1f, 0.08f, 0.05f);
                _indicator.intensity = UnityEngine.Random.value < 0.08f ? 0.5f : 0.03f;
            }
            else if (dark)
            {
                // Power out: slow red blink.
                _indicator.color = new Color(1f, 0.08f, 0.05f);
                _indicator.intensity = Mathf.PingPong(Time.time * 0.8f, 0.6f) + 0.1f;
            }
            else
            {
                // Power on: steady dim green.
                _indicator.color = new Color(0.2f, 1f, 0.3f);
                _indicator.intensity = 0.35f;
            }
        }

        private void ResetRaid()
        {
            try { RaidReport.End(); } catch (Exception ex) { Logger.LogWarning($"{LogTag} raid summary failed: {ex.Message}"); }
            StopAllCoroutines();
            _sound?.StopAll();
            _generatorLoops.Clear();
            _beacons.Remove();
            _faults?.Clear();
            _dist?.Cancel();
            _burnt?.Clear();
            _nextFalseStart = 0f;
            _stagedRestore?.ResetRaid();
            WedgeSpawn.ResetRaid();
            SecondWave.ResetRaid();
            GateSquad.ResetRaid();
            PresenceLights.ResetRaid();
            KillaStalk.ResetRaid();
            TagillaGuard.ResetRaid();
            DangerFlicker.Release();
            GameAnnouncer.ResetRaid();
            _raidSeenAt = _cutSeenAt = -1f;
            _killaReleaseAt = -1f;
            _emergency?.ResetRaid();
            RepairKit.ResetRaid();
            _repairRunning = _repairedReady = _genBDamaged = false;
            _startsThisRaid = 0;
            _whiteboardHidden = false;
            _nextWhiteboardCheck = 0f;
            _gateLamp = null;
            _bdReadyAt = -1f;
            BlackDivHunt.ResetRaid();
            _autoGens = null;
            ExtractManager.ResetRaid();
            KeycardGuard.ResetRaid();
            _failedStartRunning = false;
            _failsInRow = 0;
            _inRaid = false;
            _everCut = false;
            _toggleRequested = false;
            _nextToggleAllowed = 0f;
            _switchResetAt = 0f;
            _switchCheckAt = 0f;
            _selfClosing = false;
            _switch = null;
            _nextSnapshot = 0f;
            _missingPositionWarned = false;
            _ambient.Clear();
            if (_switchGo != null) Destroy(_switchGo);
            _switchGo = null;
            _indicator = null;
        }

        private void Show(string text, float seconds)
        {
            _msg = text;
            _msgUntil = Time.time + seconds;
        }

        private GUIStyle _timerStyle;

        private void OnGUI()
        {
            // emergency power countdown, always on screen while it lasts
            if (_emergency != null && _emergency.Active)
            {
                if (_timerStyle == null)
                {
                    _timerStyle = new GUIStyle(GUI.skin.label)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 20,
                        fontStyle = FontStyle.Bold,
                        normal = { textColor = new Color(1f, 0.25f, 0.15f) },
                    };
                }
                float r = _emergency.Remaining;
                bool blink = r < 30f && Mathf.Repeat(Time.time, 1f) < 0.35f;
                if (!blink)
                    GUI.Label(new Rect(0f, Screen.height * 0.06f, Screen.width, 32f),
                        $"EMERGENCY POWER - GATES OPEN  {(int)r / 60}:{(int)r % 60:00}", _timerStyle);
            }
            if (_msg == null || Time.time >= _msgUntil) return;
            if (_msgStyle == null)
            {
                _msgStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 18,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = new Color(1f, 0.85f, 0.3f) },
                };
            }
            GUI.Label(new Rect(0f, Screen.height * 0.12f, Screen.width, 40f), $"[Power Restore] {_msg}", _msgStyle);
        }

        private static bool TryParseVector(string s, out Vector3 v)
        {
            v = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Split(',');
            if (parts.Length != 3) return false;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        private static string FormatVector(Vector3 v) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.###}, {1:0.###}, {2:0.###}", v.x, v.y, v.z);
    }
}
