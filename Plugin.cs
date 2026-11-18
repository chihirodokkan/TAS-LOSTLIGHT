using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Mono;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

namespace LostLightTAS
{
    // Exécuté avant PlayerMovementCC.Update : caméra et touches rejouées sont en place avant que le mouvement les lise.
    [BepInPlugin("woofle.lostlight.tas", "Lost Light TAS", "1.0.0")]
    [DefaultExecutionOrder(-1000)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const string MovieHeader = "# Une ligne par frame: x y [jump] [crouch] [interact] [sprint] [respawn] [cam=yaw/pitch[/axeH/axeV]] [pos=x/y/z]";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ===== CONFIG =====
        private ConfigEntry<int> _cfgSeed, _cfgFps, _cfgPort;
        private ConfigEntry<float> _cfgThreshold;
        private ConfigEntry<bool> _cfgAutoPause, _cfgImmediate, _cfgEvents, _cfgBackup;
        private ConfigEntry<Key> _kPause, _kStep, _kSave, _kLoad, _kPlay, _kRec, _kRerec, _kHud, _kAnaStart, _kAnaStop,
            _kTurbo, _kSlotUp, _kSlotDown, _kDup, _kDel, _kUndo;
        private int Seed { get { return _cfgSeed.Value; } }

        // ===== ÉTAT TAS =====
        private readonly List<FrameInput> _frames = new List<FrameInput>();
        private readonly List<List<FrameInput>> _undo = new List<List<FrameInput>>();
        private Keyboard _keys;
        private Mouse _mouse;          // souris virtuelle (clic + delta de regard), créée à la demande
        private bool _mouseActive;     // un état non neutre a été envoyé : il faut le relâcher
        private int _playIndex;
        private bool _playing, _recording, _paused, _stepRequested, _turbo;
        private StreamWriter _recordWriter;
        private float _savedTimeScale = 1f;
        private string _inputPath, _stateDir;
        private FrameInput _hudInput = new FrameInput();
        private Texture2D _white;
        private bool _loggedFirstApply;
        private bool _immediateInput = true, _flushWarned;
        private long _tasFrameCounter;

        // ===== SAVESTATES =====
        private readonly SaveSlot[] _slots = new SaveSlot[10];
        private readonly bool[] _slotOnDisk = new bool[10];
        private int _slot = 1;

        // ===== DÉSYNCHRO =====
        private int _desyncFrame = -1;
        private float _lastDrift, _maxDrift;

        // ===== CAMÉRA =====
        // PlayerMovementCC.ApplyRotation utilise mainCam.eulerAngles.y : la caméra fait partie de l'entrée.
        private FrameInput _camPending;
        private readonly List<Behaviour> _disabledLook = new List<Behaviour>();

        // ===== ANALYSE =====
        private StreamWriter _analysisWriter;
        private string _analysisDir;
        private Vector3 _lastPos;
        private bool _hasLastPos;
        private float _distance;
        private bool _showHud = true;
        private float _fpsSmoothed = 60f;
        private readonly Queue<Vector3> _trail = new Queue<Vector3>();
        private const int TrailLength = 120;
        private static readonly FieldInfo GroundedField = typeof(PlayerMovementCC).GetField("isGrounded", InstanceFlags);
        private static readonly FieldInfo JumpsField = typeof(PlayerMovementCC).GetField("currentNumJumps", InstanceFlags);

        // ===== JOUEUR / ÉVÉNEMENTS =====
        private PlayerMovementCC _mover;
        private int _nextMoverSearch;
        private PlayerRespawn _hookedRespawn;
        private string _lastEvent = "";

        // ===== SERVEUR (appli WPF) =====
        private readonly TasServer _server = new TasServer();

        // =====================================================================
        // Initialisation
        // =====================================================================
        private void Awake()
        {
            _cfgSeed = Config.Bind("General", "Seed", 137, "Graine de UnityEngine.Random au début de chaque lecture/enregistrement.");
            _cfgFps = Config.Bind("General", "Framerate", 60, "Fréquence fixe (Time.captureFramerate).");
            _cfgImmediate = Config.Bind("Input", "ImmediateInjection", true, "Applique les touches rejouées dans la frame courante (recommandé).");
            _cfgThreshold = Config.Bind("Desync", "Threshold", 0.01f, "Écart de position (mètres) à partir duquel une désynchro est signalée.");
            _cfgAutoPause = Config.Bind("Desync", "AutoPause", true, "Met le jeu en pause à la première désynchro.");
            _cfgBackup = Config.Bind("Files", "BackupBeforeRecord", true, "Sauvegarde inputs.txt dans backups/ avant d'écraser.");
            _cfgPort = Config.Bind("Server", "Port", 7777, "Port TCP local de l'application WPF.");
            _cfgEvents = Config.Bind("Server", "SendEvents", true, "Envoie les lignes EVENT/INFO à l'appli WPF.");

            _kPause = Config.Bind("Keys", "Pause", Key.F10, "Pause / reprise");
            _kStep = Config.Bind("Keys", "Step", Key.F11, "Avancer d'une frame");
            _kSave = Config.Bind("Keys", "SaveState", Key.F3, "Sauvegarder le slot courant");
            _kLoad = Config.Bind("Keys", "LoadState", Key.F4, "Charger le slot courant");
            _kPlay = Config.Bind("Keys", "Playback", Key.F5, "Lecture du fichier d'inputs");
            _kRec = Config.Bind("Keys", "Record", Key.F6, "Enregistrement (écrase)");
            _kRerec = Config.Bind("Keys", "Rerecord", Key.F7, "Continuer l'enregistrement depuis la frame courante de lecture");
            _kHud = Config.Bind("Keys", "Hud", Key.F12, "Afficher/masquer le HUD");
            _kAnaStart = Config.Bind("Keys", "AnalysisStart", Key.F8, "Démarrer l'analyse CSV");
            _kAnaStop = Config.Bind("Keys", "AnalysisStop", Key.F9, "Arrêter l'analyse CSV");
            _kTurbo = Config.Bind("Keys", "Turbo", Key.F2, "Lecture sans limite de framerate");
            _kSlotUp = Config.Bind("Keys", "SlotUp", Key.PageUp, "Slot de savestate suivant");
            _kSlotDown = Config.Bind("Keys", "SlotDown", Key.PageDown, "Slot de savestate précédent");
            _kDup = Config.Bind("Keys", "DuplicateFrame", Key.Insert, "Dupliquer la frame courante (édition)");
            _kDel = Config.Bind("Keys", "DeleteFrame", Key.Delete, "Supprimer la frame courante (édition)");
            _kUndo = Config.Bind("Keys", "Undo", Key.Backspace, "Annuler la dernière édition");
            _immediateInput = _cfgImmediate.Value;

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = _cfgFps.Value;
            Time.captureFramerate = _cfgFps.Value;
            UnityEngine.Random.InitState(Seed);

            string root = Path.Combine(Paths.ConfigPath, "LostLightTAS");
            _inputPath = Path.Combine(root, "inputs.txt");
            _stateDir = Path.Combine(root, "states");
            _analysisDir = Path.Combine(root, "analysis");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(_stateDir);
            Directory.CreateDirectory(_analysisDir);
            for (int i = 1; i <= 9; i++) _slotOnDisk[i] = File.Exists(SlotPath(i));

            LoadFrames();
            SceneManager.activeSceneChanged += OnSceneChanged;
            Logger.LogInfo("TAS prêt. Touches configurables dans la config BepInEx (section Keys).");

            try { _server.Start(_cfgPort.Value); Logger.LogInfo("Serveur TAS sur 127.0.0.1:" + _cfgPort.Value); }
            catch (Exception e) { Logger.LogWarning("Serveur TAS impossible: " + e.Message); }
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnSceneChanged;
            UnhookRespawn();
            StopAnalysis();
            _server.Stop();
            if (_recordWriter != null) { _recordWriter.Flush(); _recordWriter.Dispose(); }
            if (_keys != null && _keys.added) InputSystem.RemoveDevice(_keys);
            if (_mouse != null && _mouse.added) InputSystem.RemoveDevice(_mouse);
            if (_white != null) Destroy(_white);
            SetLookControllers(false);
            Time.captureFramerate = 0;
            if (_paused) Time.timeScale = _savedTimeScale > 0f ? _savedTimeScale : 1f;
        }

        private void EnsureDevices()
        {
            if (_keys == null || !_keys.added)
            {
                try
                {
                    _keys = InputSystem.AddDevice<Keyboard>();
                    Logger.LogInfo("Clavier virtuel créé: " + _keys);
                }
                catch (Exception e)
                {
                    _keys = null;
                    Logger.LogWarning("Clavier virtuel impossible: " + e);
                }
            }
        }

        // =====================================================================
        // Boucle principale
        // =====================================================================
        private void Update()
        {
            EnsureDevices();
            ApplyCameraOverride();

            // Compte une frame qui a réellement commencé en lecture, ou une frame avancée manuellement.
            bool wasAdvancing = !_paused;
            HandleHotkeys();
            ProcessServerCommands();

            bool executeStep = _stepRequested;
            bool advancingFrame = wasAdvancing || executeStep;
            if (advancingFrame) _tasFrameCounter++;

            if (_playing && (!_paused || executeStep) && _frames.Count > 0)
            {
                if (_playIndex >= _frames.Count) StopPlayback();
                else if (!CheckDesync(_playIndex, _frames[_playIndex])) Apply(_frames[_playIndex++]);
            }
            else if (_recording && (!_paused || executeStep))
            {
                FrameInput human = SampleHumanKeyboard();
                ReadCamera(human);
                ReadPosition(human);
                _hudInput = human;
                WriteFrame(human);
            }
            else if (!_playing && !_recording)
            {
                // Mode libre : le HUD et l'analyse reflètent le clavier humain.
                _hudInput = SampleHumanKeyboard();
            }
            if (executeStep) _stepRequested = false;

            if (advancingFrame) RecordAnalysisFrame();
            SendStateToServer();
        }

        private void HandleHotkeys()
        {
            if (Pressed(_kPause.Value)) TogglePause();
            if (Pressed(_kStep.Value)) StepOneFrame();
            if (Pressed(_kSave.Value)) SaveState(_slot);
            if (Pressed(_kLoad.Value)) LoadState(_slot);
            if (Pressed(_kPlay.Value)) TogglePlayback();
            if (Pressed(_kRec.Value)) ToggleRecording();
            if (Pressed(_kRerec.Value)) StartRerecord();
            if (Pressed(_kHud.Value)) _showHud = !_showHud;
            if (Pressed(_kAnaStart.Value)) StartAnalysis();
            if (Pressed(_kAnaStop.Value)) StopAnalysis();
            if (Pressed(_kTurbo.Value)) ToggleTurbo();
            if (Pressed(_kSlotUp.Value)) ChangeSlot(1);
            if (Pressed(_kSlotDown.Value)) ChangeSlot(-1);
            if (Pressed(_kDup.Value)) InsertFrame(_playIndex, null);
            if (Pressed(_kDel.Value)) DeleteFrames(_playIndex, 1);
            if (Pressed(_kUndo.Value)) Undo();
        }

        private bool Pressed(Key key)
        {
            foreach (var device in InputSystem.devices)
            {
                if (device is Keyboard kb && !IsTasDevice(kb) && kb[key].wasPressedThisFrame)
                    return true;
            }
            return false;
        }

        private bool IsTasDevice(InputDevice device) { return device == _keys || device == _mouse; }

        // Envoie clic gauche + delta de regard. N'ajoute la souris virtuelle que si le film en a besoin.
        private void ApplyMouse(FrameInput f)
        {
            bool needed = f.Click || f.Look != Vector2.zero;
            if (!needed && !_mouseActive) return;
            try
            {
                if (_mouse == null || !_mouse.added) _mouse = InputSystem.AddDevice<Mouse>();
                var ms = new MouseState { delta = f.Look };
                if (f.Click) ms = ms.WithButton(MouseButton.Left, true);
                InputSystem.QueueStateEvent(_mouse, ms);
                _mouseActive = needed;
            }
            catch (Exception e) { Logger.LogWarning("Apply souris: " + e.Message); _mouseActive = false; }
        }

        private FrameInput SampleHumanMouse(FrameInput f)
        {
            Mouse m = InputSystem.devices.OfType<Mouse>().FirstOrDefault(x => !IsTasDevice(x));
            if (m == null) return f;
            f.Click = m.leftButton.isPressed;
            f.Look = m.delta.ReadValue();
            return f;
        }

        // =====================================================================
        // Serveur
        // =====================================================================
        private void ProcessServerCommands()
        {
            while (_server.Commands.TryDequeue(out string cmd))
            {
                try
                {
                    string[] parts = cmd.Split(new[] { ' ' }, 2);
                    string name = parts[0].ToUpperInvariant();
                    string arg = parts.Length > 1 ? parts[1].Trim() : "";
                    int n;

                    switch (name)
                    {
                        case "PAUSE": TogglePause(); break;
                        case "STEP": StepOneFrame(); break;
                        case "SAVE": SaveState(int.TryParse(arg, out n) ? n : _slot); break;
                        case "LOAD": LoadState(int.TryParse(arg, out n) ? n : _slot); break;
                        case "SLOT": if (int.TryParse(arg, out n)) SetSlot(n); break;
                        case "PLAY": TogglePlayback(); break;
                        case "REC": ToggleRecording(); break;
                        case "RERECORD": StartRerecord(); break;
                        case "TURBO": ToggleTurbo(); break;
                        case "ANALYSE_START": StartAnalysis(); break;
                        case "ANALYSE_STOP": StopAnalysis(); break;
                        case "UNDO": Undo(); break;
                        case "STATUS": SendStatus(); break;

                        case "RELOAD":
                            LoadFrames();
                            Logger.LogInfo("Frames rechargées: " + _frames.Count);
                            break;

                        case "GOTO":
                            if (int.TryParse(arg, out n))
                                _playIndex = Mathf.Clamp(n, 0, _frames.Count);
                            break;

                        case "SET":
                        {
                            string[] a = arg.Split(new[] { ' ' }, 2);
                            if (a.Length == 2 && int.TryParse(a[0], out n)) SetFrame(n, FrameInput.Parse(a[1]));
                            break;
                        }
                        case "INSERT":
                        {
                            string[] a = arg.Split(new[] { ' ' }, 2);
                            if (a.Length >= 1 && int.TryParse(a[0], out n))
                                InsertFrame(n, a.Length == 2 ? FrameInput.Parse(a[1]) : null);
                            break;
                        }
                        case "DELETE":
                        {
                            string[] a = arg.Split(new[] { ' ' }, 2);
                            if (a.Length >= 1 && int.TryParse(a[0], out n))
                            {
                                int count = 1;
                                if (a.Length == 2) int.TryParse(a[1], out count);
                                DeleteFrames(n, count);
                            }
                            break;
                        }

                        default:
                            Logger.LogWarning("Commande inconnue: " + cmd);
                            break;
                    }
                }
                catch (Exception e) { Logger.LogWarning("Commande '" + cmd + "' échouée: " + e.Message); }
            }
        }

        private void SendStateToServer()
        {
            if (!_server.HasClient) return;
            PlayerMovementCC mover = FindMover();
            if (mover == null) return;

            Vector3 p = mover.transform.position;
            CharacterController cc = mover.GetComponent<CharacterController>();
            Vector3 v = cc != null ? cc.velocity : Vector3.zero;
            int frame = _playing ? _playIndex : Time.frameCount;
            _server.SendState(frame, p.x, p.y, p.z, v.x, v.y, v.z, SceneManager.GetActiveScene().name);
        }

        private void SendEvent(string text)
        {
            _lastEvent = text;
            try
            {
                PlayerMovementCC m = _mover;
                string pos = m != null ? m.transform.position.ToString("F2") : "-";
                File.AppendAllText(Path.Combine(_analysisDir, "events.log"),
                    "frame=" + Time.frameCount + " tas=" + _tasFrameCounter + " t=" + Time.time.ToString("F3", Inv) + " " + text + " pos=" + pos + Environment.NewLine);
            }
            catch (Exception) { }
            if (_cfgEvents.Value) _server.SendLine("EVENT " + text);
        }

        private void SendStatus()
        {
            string mode = _paused ? "PAUSE" : _playing ? "PLAY" : _recording ? "REC" : "IDLE";
            _server.SendLine("INFO mode=" + mode + " tas=" + _tasFrameCounter + " play=" + _playIndex + "/" + _frames.Count +
                             " slot=" + _slot + " turbo=" + (_turbo ? 1 : 0) + " desync=" + _desyncFrame +
                             " drift=" + _lastDrift.ToString("0.####", Inv));
        }

        // =====================================================================
        // Pause / pas à pas / vitesse
        // =====================================================================
        private System.Collections.IEnumerator FreezeAfterOneRenderedFrame()
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForEndOfFrame();
            Time.timeScale = 0f;
            _paused = true;
        }

        private void TogglePause()
        {
            if (_paused) { _paused = false; Time.timeScale = _savedTimeScale <= 0f ? 1f : _savedTimeScale; }
            else { _savedTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f; _paused = true; Time.timeScale = 0f; }
        }

        private void StepOneFrame()
        {
            if (!_paused) TogglePause();
            _stepRequested = true;
            Time.timeScale = 1f;
            StartCoroutine(FreezeAfterOneRenderedFrame());
        }

        private void ToggleTurbo()
        {
            _turbo = !_turbo;
            Application.targetFrameRate = _turbo ? -1 : _cfgFps.Value;
            Logger.LogInfo("Turbo " + (_turbo ? "activé" : "désactivé"));
        }

        // =====================================================================
        // Lecture
        // =====================================================================
        private void TogglePlayback()
        {
            if (_playing) { StopPlayback(); return; }
            if (_recording) { Logger.LogWarning("Arrête l'enregistrement avant de lancer la lecture."); return; }
            LoadFrames();
            if (_frames.Count == 0) { Logger.LogWarning("Aucune frame lisible dans " + _inputPath); return; }
            UnityEngine.Random.InitState(Seed);
            _camPending = null;
            if (_frames.Any(x => x.HasCam)) SetLookControllers(true);
            else Logger.LogWarning("Aucune donnée caméra dans le fichier: la relecture suivra la souris réelle (non déterministe).");
            if (!_frames.Any(x => x.HasPos)) Logger.LogInfo("Pas de positions de référence (pos=) : détection de désynchro inactive.");
            _desyncFrame = -1; _lastDrift = 0f; _maxDrift = 0f;
            _playIndex = 0; _playing = true; _loggedFirstApply = false;
            Logger.LogInfo("Lecture TAS démarrée: " + _frames.Count + " frames. Clavier=" + (_keys != null));
            SendEvent("PLAY " + _frames.Count);
        }

        private void StopPlayback()
        {
            _playing = false;
            Apply(new FrameInput());
            _camPending = null;
            SetLookControllers(false);
            Logger.LogInfo("Lecture TAS terminée/arrêtée.");
            SendEvent("STOP " + _playIndex);
        }

        // Compare la position réelle à celle enregistrée. Retourne true si le jeu a été mis en pause.
        private bool CheckDesync(int idx, FrameInput f)
        {
            if (!f.HasPos) return false;
            PlayerMovementCC m = FindMover();
            if (m == null) return false;
            float d = Vector3.Distance(f.Pos, m.transform.position);
            _lastDrift = d;
            if (d > _maxDrift) _maxDrift = d;
            if (d > _cfgThreshold.Value && _desyncFrame < 0)
            {
                _desyncFrame = idx;
                Logger.LogWarning("DÉSYNCHRO à la frame " + idx + ": écart " + d.ToString("0.#####", Inv) + " m");
                SendEvent("DESYNC " + idx + " " + d.ToString("0.#####", Inv));
                if (_cfgAutoPause.Value && !_paused) { TogglePause(); return true; }
            }
            return false;
        }

        // =====================================================================
        // Fichier d'inputs
        // =====================================================================
        private void LoadFrames()
        {
            _frames.Clear();
            if (!File.Exists(_inputPath)) return;
            int lineNo = 0;
            foreach (string line in File.ReadLines(_inputPath))
            {
                lineNo++;
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
                try { _frames.Add(FrameInput.Parse(line)); }
                catch (Exception e) { Logger.LogWarning("Ligne " + lineNo + " ignorée: " + e.Message); }
            }
        }

        // Écriture atomique : fichier temporaire puis renommage.
        private void SaveFramesAtomic()
        {
            try
            {
                string tmp = _inputPath + ".tmp";
                using (var w = new StreamWriter(tmp, false))
                {
                    w.WriteLine(MovieHeader);
                    foreach (FrameInput f in _frames) w.WriteLine(f.ToLine());
                }
                if (File.Exists(_inputPath)) File.Delete(_inputPath);
                File.Move(tmp, _inputPath);
            }
            catch (Exception e) { Logger.LogError("Écriture du fichier d'inputs impossible: " + e.Message); }
        }

        private void BackupInputs()
        {
            if (!_cfgBackup.Value || !File.Exists(_inputPath)) return;
            try
            {
                if (new FileInfo(_inputPath).Length < 64) return;
                string dir = Path.Combine(Path.GetDirectoryName(_inputPath), "backups");
                Directory.CreateDirectory(dir);
                File.Copy(_inputPath, Path.Combine(dir, "inputs_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"), true);
                foreach (FileInfo old in new DirectoryInfo(dir).GetFiles("inputs_*.txt").OrderByDescending(f => f.CreationTimeUtc).Skip(30))
                    old.Delete();
            }
            catch (Exception e) { Logger.LogWarning("Sauvegarde de secours impossible: " + e.Message); }
        }

        // =====================================================================
        // Enregistrement / rerecord
        // =====================================================================
        private void ToggleRecording()
        {
            if (_recording)
            {
                _recording = false;
                if (_recordWriter != null) { _recordWriter.Flush(); _recordWriter.Dispose(); _recordWriter = null; }
                Logger.LogInfo("Enregistrement écrit: " + _inputPath + " (" + _frames.Count + " frames)");
                SendEvent("REC_STOP " + _frames.Count);
                return;
            }
            if (_playing) StopPlayback();
            try
            {
                BackupInputs();
                _frames.Clear();
                OpenRecordWriter();
                UnityEngine.Random.InitState(Seed);
                _recording = true;
                Logger.LogInfo("Enregistrement clavier humain démarré.");
                SendEvent("REC_START");
            }
            catch (Exception e) { Logger.LogError("Ouverture enregistrement impossible: " + e); }
        }

        // Reprend l'enregistrement à partir de la frame de lecture courante (typiquement après un LOAD).
        private void StartRerecord()
        {
            if (_recording) { ToggleRecording(); return; }
            try
            {
                if (_frames.Count == 0) LoadFrames();
                int cut = Mathf.Clamp(_playIndex, 0, _frames.Count);
                if (_playing) StopPlayback();
                PushUndo();
                BackupInputs();
                _frames.RemoveRange(cut, _frames.Count - cut);
                OpenRecordWriter();
                foreach (FrameInput f in _frames) _recordWriter.WriteLine(f.ToLine());
                _recordWriter.Flush();
                _recording = true;
                Logger.LogInfo("Rerecord depuis la frame " + cut + " (" + _frames.Count + " frames conservées).");
                SendEvent("RERECORD " + cut);
            }
            catch (Exception e) { Logger.LogError("Rerecord impossible: " + e); }
        }

        private void OpenRecordWriter()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_inputPath));
            _recordWriter = new StreamWriter(_inputPath, false);
            _recordWriter.WriteLine(MovieHeader);
        }

        private void WriteFrame(FrameInput f)
        {
            _frames.Add(f);
            _playIndex = _frames.Count;
            if (_recordWriter == null) return;
            _recordWriter.WriteLine(f.ToLine());
            if (Time.frameCount % 60 == 0) _recordWriter.Flush();
        }

        // Le jeu lie Move à <Keyboard>/w,a,s,d (positions physiques, comme les flèches) : sur AZERTY ce sont les touches Z,Q,S,D.
        private FrameInput SampleHumanKeyboard()
        {
            var f = new FrameInput();
            Keyboard kb = InputSystem.devices.OfType<Keyboard>().FirstOrDefault(k => !IsTasDevice(k));
            if (kb == null) return f;
            bool up = kb.wKey.isPressed || kb.upArrowKey.isPressed;
            bool down = kb.sKey.isPressed || kb.downArrowKey.isPressed;
            bool left = kb.aKey.isPressed || kb.leftArrowKey.isPressed;
            bool right = kb.dKey.isPressed || kb.rightArrowKey.isPressed;
            f.Move = new Vector2((right ? 1 : 0) - (left ? 1 : 0), (up ? 1 : 0) - (down ? 1 : 0));
            f.Jump = kb.spaceKey.isPressed;
            f.Crouch = kb.leftCtrlKey.isPressed;
            f.Interact = kb.eKey.isPressed;
            f.Sprint = kb.leftBracketKey.isPressed;   // liaison clavier du jeu : [
            f.Respawn = kb.rKey.isPressed;
            SampleHumanMouse(f);
            return f;
        }

        private void ReadPosition(FrameInput f)
        {
            PlayerMovementCC m = FindMover();
            if (m == null) return;
            f.HasPos = true;
            f.Pos = m.transform.position;
        }

        // =====================================================================
        // Édition des frames (avec annulation)
        // =====================================================================
        private void PushUndo()
        {
            _undo.Add(_frames.Select(f => f.Clone()).ToList());
            if (_undo.Count > 30) _undo.RemoveAt(0);
        }

        private void Undo()
        {
            if (_undo.Count == 0) { Logger.LogWarning("Rien à annuler."); return; }
            if (_recording) { Logger.LogWarning("Impossible d'annuler pendant l'enregistrement."); return; }
            List<FrameInput> prev = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _frames.Clear();
            _frames.AddRange(prev);
            _playIndex = Mathf.Clamp(_playIndex, 0, _frames.Count);
            SaveFramesAtomic();
            Logger.LogInfo("Édition annulée (" + _frames.Count + " frames).");
        }

        // Les positions de référence après un point d'édition ne sont plus valables.
        private void InvalidatePositionsFrom(int idx)
        {
            for (int i = Mathf.Max(0, idx); i < _frames.Count; i++) _frames[i].HasPos = false;
        }

        private bool CanEdit()
        {
            if (_recording) { Logger.LogWarning("Édition impossible pendant l'enregistrement."); return false; }
            if (_frames.Count == 0) LoadFrames();
            return true;
        }

        private void SetFrame(int idx, FrameInput f)
        {
            if (!CanEdit() || idx < 0 || idx >= _frames.Count) { Logger.LogWarning("SET: index invalide " + idx); return; }
            PushUndo();
            if (!f.HasCam && _frames[idx].HasCam) { f.HasCam = true; f.HasAxis = _frames[idx].HasAxis; f.Yaw = _frames[idx].Yaw; f.Pitch = _frames[idx].Pitch; f.AxisH = _frames[idx].AxisH; f.AxisV = _frames[idx].AxisV; }
            _frames[idx] = f;
            InvalidatePositionsFrom(idx);
            SaveFramesAtomic();
            Logger.LogInfo("Frame " + idx + " modifiée.");
        }

        // frame == null : duplique la frame à l'index (ou la dernière).
        private void InsertFrame(int idx, FrameInput frame)
        {
            if (!CanEdit()) return;
            if (frame == null)
            {
                if (_frames.Count == 0) return;
                idx = Mathf.Clamp(idx, 0, _frames.Count - 1);
                frame = _frames[idx].Clone();
            }
            idx = Mathf.Clamp(idx, 0, _frames.Count);
            PushUndo();
            _frames.Insert(idx, frame);
            InvalidatePositionsFrom(idx);
            SaveFramesAtomic();
            Logger.LogInfo("Frame insérée à " + idx + " (" + _frames.Count + " frames).");
        }

        private void DeleteFrames(int idx, int count)
        {
            if (!CanEdit()) return;
            if (idx < 0 || idx >= _frames.Count || count <= 0) { Logger.LogWarning("DELETE: index invalide " + idx); return; }
            count = Mathf.Min(count, _frames.Count - idx);
            PushUndo();
            _frames.RemoveRange(idx, count);
            _playIndex = Mathf.Clamp(_playIndex, 0, _frames.Count);
            InvalidatePositionsFrom(idx);
            SaveFramesAtomic();
            Logger.LogInfo(count + " frame(s) supprimée(s) à partir de " + idx + " (" + _frames.Count + " frames).");
        }

        // =====================================================================
        // Injection des touches
        // =====================================================================
        private void FlushInput()
        {
            try { InputSystem.Update(); }
            catch (Exception e)
            {
                if (!_flushWarned) Logger.LogWarning("InputSystem.Update impossible, injection différée d'une frame: " + e.Message);
                _flushWarned = true;
                _immediateInput = false;
            }
        }

        // Injecte les touches telles que le jeu les lie (Key.W/A/S/D = Z/Q/S/D sur AZERTY).
        private void Apply(FrameInput f)
        {
            _hudInput = f;
            _camPending = f;

            if (_keys != null && _keys.added)
            {
                try
                {
                    var kstate = new KeyboardState();
                    if (f.Move.y > 0.3f) kstate.Set(Key.W, true);
                    if (f.Move.y < -0.3f) kstate.Set(Key.S, true);
                    if (f.Move.x > 0.3f) kstate.Set(Key.D, true);
                    if (f.Move.x < -0.3f) kstate.Set(Key.A, true);
                    if (f.Jump) kstate.Set(Key.Space, true);
                    if (f.Crouch) kstate.Set(Key.LeftCtrl, true);
                    if (f.Interact) kstate.Set(Key.E, true);
                    if (f.Sprint) kstate.Set(Key.LeftBracket, true);
                    if (f.Respawn) kstate.Set(Key.R, true);
                    InputSystem.QueueStateEvent(_keys, kstate);
                    if (_immediateInput) FlushInput();
                }
                catch (Exception e) { Logger.LogWarning("Apply clavier: " + e.Message); }
            }
            ApplyMouse(f);
            if (_immediateInput) { FlushInput(); ApplyCameraOverride(); }

            if (!_loggedFirstApply && (f.Move != Vector2.zero || f.Jump))
            {
                _loggedFirstApply = true;
                Logger.LogInfo("Première frame avec input appliquée: move=" + f.Move + " jump=" + f.Jump);
            }
        }

        // =====================================================================
        // Caméra
        // =====================================================================
        private static Unity.Cinemachine.CinemachineOrbitalFollow GetOrbital()
        {
            if (Unity.Cinemachine.CinemachineBrain.ActiveBrainCount == 0) return null;
            var vcam = Unity.Cinemachine.CinemachineBrain.GetActiveBrain(0).ActiveVirtualCamera;
            if (vcam == null) return null;
            Component comp = vcam as Component;
            return comp != null ? comp.GetComponent<Unity.Cinemachine.CinemachineOrbitalFollow>() : null;
        }

        // Désactive (ou réactive) la lecture souris/stick droit de Cinemachine pendant la relecture.
        private void SetLookControllers(bool disable)
        {
            try
            {
                if (disable)
                {
                    foreach (var c in UnityEngine.Object.FindObjectsByType<Unity.Cinemachine.CinemachineInputAxisController>(FindObjectsSortMode.None))
                        if (c.enabled) { c.enabled = false; _disabledLook.Add(c); }
                }
                else
                {
                    foreach (Behaviour c in _disabledLook) if (c != null) c.enabled = true;
                    _disabledLook.Clear();
                }
            }
            catch (Exception e) { Logger.LogWarning("Contrôleurs de caméra: " + e.Message); }
        }

        private void ReadCamera(FrameInput f)
        {
            try
            {
                Camera cam = Camera.main;
                if (cam == null) return;
                Vector3 e = cam.transform.eulerAngles;
                f.HasCam = true; f.Yaw = e.y; f.Pitch = e.x;
                var orbital = GetOrbital();
                if (orbital != null)
                {
                    f.HasAxis = true;
                    f.AxisH = orbital.HorizontalAxis.Value;
                    f.AxisV = orbital.VerticalAxis.Value;
                }
            }
            catch (Exception ex) { Logger.LogWarning("Lecture caméra: " + ex.Message); }
        }

        private void ApplyCamera(FrameInput c)
        {
            if (c == null || !c.HasCam) return;
            try
            {
                Camera cam = Camera.main;
                if (cam == null) return;
                cam.transform.rotation = Quaternion.Euler(c.Pitch, c.Yaw, 0f);
                if (c.HasAxis)
                {
                    var orbital = GetOrbital();
                    if (orbital != null)
                    {
                        var h = orbital.HorizontalAxis; h.Value = c.AxisH; orbital.HorizontalAxis = h;
                        var v = orbital.VerticalAxis; v.Value = c.AxisV; orbital.VerticalAxis = v;
                    }
                }
            }
            catch (Exception ex) { Logger.LogWarning("Application caméra: " + ex.Message); }
        }

        // Sans ImmediateInjection, la caméra de la ligne N est appliquée à la frame où le clavier de la ligne N est consommé.
        private void ApplyCameraOverride()
        {
            if (!_playing) return;
            if (Time.frameCount % 30 == 0 && _camPending != null && _camPending.HasCam) SetLookControllers(true);
            ApplyCamera(_camPending);
        }

        // =====================================================================
        // Joueur, scène, événements
        // =====================================================================
        // Résultat mis en cache : la recherche n'est refaite que si le joueur a disparu (max 1 fois / 30 frames).
        private PlayerMovementCC FindMover()
        {
            if (_mover != null) return _mover;
            if (Time.frameCount < _nextMoverSearch) return null;
            _nextMoverSearch = Time.frameCount + 30;
            _mover = UnityEngine.Object.FindFirstObjectByType<PlayerMovementCC>();
            if (_mover != null) HookRespawn(_mover.GetComponent<PlayerRespawn>());
            return _mover;
        }

        // PlayerRespawn.OnRespawned est un événement statique : un seul abonnement suffit.
        private void HookRespawn(PlayerRespawn r)
        {
            if (r == null || _hookedRespawn != null) return;
            _hookedRespawn = r;
            PlayerRespawn.OnRespawned += OnPlayerRespawned;
        }

        private void UnhookRespawn()
        {
            if (_hookedRespawn != null) PlayerRespawn.OnRespawned -= OnPlayerRespawned;
            _hookedRespawn = null;
        }

        private void OnPlayerRespawned()
        {
            Logger.LogInfo("Respawn à la frame TAS " + _tasFrameCounter);
            SendEvent("RESPAWN " + _tasFrameCounter);
        }

        private void OnSceneChanged(Scene from, Scene to)
        {
            _mover = null;
            _nextMoverSearch = 0;
            _hasLastPos = false;
            _trail.Clear();
            Logger.LogInfo("Changement de scène: " + from.name + " -> " + to.name + " (frame TAS " + _tasFrameCounter + ")");
            SendEvent("SCENE " + to.name + " " + _tasFrameCounter);
        }

        // =====================================================================
        // Savestates (9 slots, mémoire + disque)
        // =====================================================================
        private string SlotPath(int slot) { return Path.Combine(_stateDir, "slot" + slot + ".sav"); }

        private void ChangeSlot(int delta) { SetSlot(((_slot - 1 + delta + 9) % 9) + 1); }

        private void SetSlot(int slot)
        {
            if (slot < 1 || slot > 9) { Logger.LogWarning("Slot invalide: " + slot); return; }
            _slot = slot;
            Logger.LogInfo("Slot " + _slot + (_slots[_slot] != null || _slotOnDisk[_slot] ? " (utilisé)" : " (vide)"));
        }

        private void SaveState(int slot)
        {
            if (slot < 1 || slot > 9) { Logger.LogWarning("Slot invalide: " + slot); return; }
            PlayerMovementCC mover = FindMover();
            if (mover == null) { Logger.LogWarning("PlayerMovementCC introuvable; état non sauvegardé."); return; }
            PlayerSnapshot snap = PlayerSnapshot.Capture(mover, mover.GetComponent<PlayerRespawn>());
            snap.Scene = SceneManager.GetActiveScene().name;
            snap.Cam = new FrameInput();
            ReadCamera(snap.Cam);
            _slots[slot] = new SaveSlot { Snap = snap, PlayIndex = _playIndex, TasFrame = _tasFrameCounter };
            try
            {
                Directory.CreateDirectory(_stateDir);
                File.WriteAllText(SlotPath(slot), snap.ToText(_playIndex, _tasFrameCounter));
                _slotOnDisk[slot] = true;
            }
            catch (Exception e) { Logger.LogWarning("Savestate disque impossible: " + e.Message); }
            Logger.LogInfo("État sauvegardé dans le slot " + slot + " (scène " + snap.Scene + ", frame de lecture " + _playIndex + ").");
            SendEvent("SAVE " + slot);
        }

        private void LoadState(int slot)
        {
            if (slot < 1 || slot > 9) { Logger.LogWarning("Slot invalide: " + slot); return; }
            if (_recording) { Logger.LogWarning("Chargement impossible pendant l'enregistrement (utilise le rerecord)."); return; }
            SaveSlot s = _slots[slot] ?? TryReadSlot(slot);
            if (s == null) { Logger.LogWarning("Slot " + slot + " vide."); return; }
            PlayerMovementCC mover = FindMover();
            if (mover == null) { Logger.LogWarning("PlayerMovementCC introuvable; état non chargé."); return; }
            string scene = SceneManager.GetActiveScene().name;
            if (s.Snap.Scene != scene) { Logger.LogWarning("Le slot " + slot + " vient de la scène '" + s.Snap.Scene + "' (actuelle: '" + scene + "')."); return; }

            s.Snap.Restore(mover, mover.GetComponent<PlayerRespawn>());
            ApplyCamera(s.Snap.Cam);
            _tasFrameCounter = s.TasFrame;
            _playIndex = Mathf.Clamp(s.PlayIndex, 0, _frames.Count);
            _camPending = null;
            _hasLastPos = false;
            _trail.Clear();
            _desyncFrame = -1; _lastDrift = 0f; _maxDrift = 0f;
            Logger.LogInfo("Slot " + slot + " restauré (frame de lecture " + _playIndex + "). Objets/coroutines non rembobinés.");
            SendEvent("LOAD " + slot);
        }

        private SaveSlot TryReadSlot(int slot)
        {
            try
            {
                string path = SlotPath(slot);
                if (!File.Exists(path)) return null;
                int playIndex; long tasFrame;
                PlayerSnapshot snap = PlayerSnapshot.FromText(File.ReadAllText(path), out playIndex, out tasFrame);
                var s = new SaveSlot { Snap = snap, PlayIndex = playIndex, TasFrame = tasFrame };
                _slots[slot] = s;
                return s;
            }
            catch (Exception e) { Logger.LogWarning("Lecture du slot " + slot + " impossible: " + e.Message); return null; }
        }

        // =====================================================================
        // Analyse CSV
        // =====================================================================
        private void StartAnalysis()
        {
            if (_analysisWriter != null) return;
            string file = Path.Combine(_analysisDir, "run_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            try
            {
                _analysisWriter = new StreamWriter(file, false);
                _analysisWriter.WriteLine("frame;tasframe;time;x;y;z;speed;dist;keys;axisH;axisV;yaw;grounded;jumps;scene;timescale;drift");
                _hasLastPos = false;
                _distance = 0f;
                Logger.LogInfo("Analyse démarrée: " + file);
            }
            catch (Exception e) { Logger.LogError("Analyse impossible: " + e); }
        }

        private void StopAnalysis()
        {
            if (_analysisWriter == null) return;
            _analysisWriter.Flush();
            _analysisWriter.Dispose();
            _analysisWriter = null;
            Logger.LogInfo("Analyse arrêtée. Distance totale: " + _distance.ToString("F2", Inv));
        }

        private void RecordAnalysisFrame()
        {
            PlayerMovementCC mover = FindMover();
            if (mover == null) return;

            Vector3 pos = mover.transform.position;
            if (_hasLastPos)
            {
                float step = Vector3.Distance(pos, _lastPos);
                if (step < 5f) _distance += step;
            }
            _lastPos = pos;
            _hasLastPos = true;

            _trail.Enqueue(pos);
            while (_trail.Count > TrailLength) _trail.Dequeue();

            _fpsSmoothed = Mathf.Lerp(_fpsSmoothed, 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f), 0.05f);

            if (_analysisWriter == null) return;

            CharacterController cc = mover.GetComponent<CharacterController>();
            float speed = cc != null ? cc.velocity.magnitude : 0f;

            var keys = new StringBuilder(9);
            keys.Append(_hudInput.Move.y > 0.3f ? '1' : '0');
            keys.Append(_hudInput.Move.y < -0.3f ? '1' : '0');
            keys.Append(_hudInput.Move.x < -0.3f ? '1' : '0');
            keys.Append(_hudInput.Move.x > 0.3f ? '1' : '0');
            keys.Append(_hudInput.Jump ? '1' : '0');
            keys.Append(_hudInput.Crouch ? '1' : '0');
            keys.Append(_hudInput.Interact ? '1' : '0');
            keys.Append(_hudInput.Sprint ? '1' : '0');
            keys.Append(_hudInput.Respawn ? '1' : '0');

            bool grounded = GroundedField != null && (bool)GroundedField.GetValue(mover);
            int jumps = JumpsField != null ? (int)JumpsField.GetValue(mover) : 0;
            Camera cam = Camera.main;
            float yaw = cam != null ? cam.transform.eulerAngles.y : 0f;

            _analysisWriter.WriteLine(string.Join(";", new[]
            {
                Time.frameCount.ToString(Inv),
                _tasFrameCounter.ToString(Inv),
                Time.time.ToString("F4", Inv),
                pos.x.ToString("F4", Inv),
                pos.y.ToString("F4", Inv),
                pos.z.ToString("F4", Inv),
                speed.ToString("F3", Inv),
                _distance.ToString("F3", Inv),
                keys.ToString(),
                _hudInput.Move.x.ToString("F2", Inv),
                _hudInput.Move.y.ToString("F2", Inv),
                yaw.ToString("F3", Inv),
                grounded ? "1" : "0",
                jumps.ToString(Inv),
                SceneManager.GetActiveScene().name,
                Time.timeScale.ToString("F2", Inv),
                _lastDrift.ToString("F5", Inv)
            }));

            if (Time.frameCount % 60 == 0) _analysisWriter.Flush();
        }

        // =====================================================================
        // HUD
        // =====================================================================
        private void DrawTrail()
        {
            if (_trail.Count < 2) return;
            Rect box = new Rect(Screen.width - 230, Screen.height - 230, 210, 210);
            DrawRect(box, new Color(0f, 0f, 0f, 0.55f));

            Vector3[] pts = _trail.ToArray();
            Vector3 min = pts[0], max = pts[0];
            foreach (var p in pts) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            float span = Mathf.Max(max.x - min.x, max.z - min.z, 1f);

            for (int i = 1; i < pts.Length; i++)
            {
                Vector2 a = ToBox(pts[i - 1], min, span, box);
                Vector2 b = ToBox(pts[i], min, span, box);
                DrawLine(a, b, new Color(0.3f, 0.9f, 1f, 0.9f));
            }
            Vector2 head = ToBox(pts[pts.Length - 1], min, span, box);
            DrawRect(new Rect(head.x - 3, head.y - 3, 6, 6), Color.yellow);
        }

        private Vector2 ToBox(Vector3 p, Vector3 min, float span, Rect box)
        {
            float nx = (p.x - min.x) / span;
            float nz = (p.z - min.z) / span;
            return new Vector2(box.x + 10 + nx * (box.width - 20), box.yMax - 10 - nz * (box.height - 20));
        }

        private void DrawLine(Vector2 a, Vector2 b, Color c)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.5f) return;
            float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, a);
            DrawRect(new Rect(a.x, a.y - 1, len, 2), c);
            GUI.matrix = old;
        }

        private void DrawHudText(float x, float y, float width, string text, Color color)
        {
            GUI.color = Color.black;
            GUI.Label(new Rect(x + 1f, y + 1f, width, 20f), text);
            GUI.color = color;
            GUI.Label(new Rect(x, y, width, 20f), text);
            GUI.color = Color.white;
        }

        private string GetGamepadStatus()
        {
            Gamepad pad = Gamepad.current;
            if (pad == null || !pad.added) return "Manette: absente";
            Vector2 stick = pad.leftStick.ReadValue();
            string buttons = "";
            if (pad.buttonSouth.isPressed) buttons += " A";
            if (pad.buttonEast.isPressed) buttons += " B";
            if (pad.buttonWest.isPressed) buttons += " X";
            if (pad.buttonNorth.isPressed) buttons += " Y";
            if (pad.leftShoulder.isPressed) buttons += " L1";
            if (pad.rightShoulder.isPressed) buttons += " R1";
            if (string.IsNullOrEmpty(buttons)) buttons = " aucune touche";
            return "Manette: " + pad.displayName + "  stick " + stick.ToString("F2") + buttons;
        }

        private static string FormatTime(float seconds)
        {
            int m = (int)(seconds / 60f);
            return m.ToString("00") + ":" + (seconds - m * 60f).ToString("00.00", Inv);
        }

        private void OnGUI()
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }

            // OnGUI est appelé plusieurs fois par frame (Layout, Repaint, events) : on ne dessine qu'une fois.
            if (!_showHud || Event.current.type != EventType.Repaint) return;

            // Panneau central : état TAS
            DrawRect(new Rect(318f, 10f, 450f, 134f), new Color(0f, 0f, 0f, 0.72f));
            string hudMode = _paused ? "PAUSE" : _playing ? "LECTURE TAS" : _recording ? "ENREGISTREMENT" : "LIBRE";
            DrawHudText(328f, 16f, 430f, "TAS frame " + _tasFrameCounter + "  (" + FormatTime(_tasFrameCounter / (float)Math.Max(1, _cfgFps.Value)) + ")   |   " + hudMode + (_turbo ? "  TURBO" : ""), Color.yellow);
            DrawHudText(328f, 38f, 430f, "Film " + _playIndex + " / " + _frames.Count + "   |   Slot " + _slot + (_slots[_slot] != null || _slotOnDisk[_slot] ? " (plein)" : " (vide)") + "   |   undo " + _undo.Count, Color.yellow);

            bool hasRef = _playing && _frames.Count > 0 && _frames.Any(x => x.HasPos);
            if (_desyncFrame >= 0)
                DrawHudText(328f, 60f, 430f, "DÉSYNCHRO frame " + _desyncFrame + "   écart max " + _maxDrift.ToString("0.###", Inv) + " m", Color.red);
            else if (hasRef)
                DrawHudText(328f, 60f, 430f, "Synchro OK   écart " + _lastDrift.ToString("0.#####", Inv) + " m", Color.green);
            else
                DrawHudText(328f, 60f, 430f, GetGamepadStatus(), Color.yellow);

            DrawHudText(328f, 82f, 430f, "F10 pause F11 step F2 turbo F3/F4 save/load PgUp/PgDn slot", Color.white);
            DrawHudText(328f, 104f, 430f, string.IsNullOrEmpty(_lastEvent) ? "F5 lecture F6 rec F7 rerecord Ins/Suppr édition Ret.arr undo" : "Dernier événement: " + _lastEvent, Color.white);

            // Panneau clavier (bas gauche)
            float x0 = 20f, y0 = Screen.height - 250f;
            DrawRect(new Rect(x0 - 10, y0 - 10, 260, 240), new Color(0f, 0f, 0f, 0.6f));

            //   [ Z ]
            // [Q][S][D]
            // [CTRL][ESPACE][E]
            // [SPRINT][R]
            float k = 46f, g = 4f;

            bool up    = _hudInput.Move.y > 0.3f;
            bool down  = _hudInput.Move.y < -0.3f;
            bool left  = _hudInput.Move.x < -0.3f;
            bool right = _hudInput.Move.x > 0.3f;

            DrawKey(x0 + k + g,            y0,                  k,        "Z",      up);
            DrawKey(x0,                    y0 + k + g,          k,        "Q",      left);
            DrawKey(x0 + k + g,            y0 + k + g,          k,        "S",      down);
            DrawKey(x0 + 2 * (k + g),      y0 + k + g,          k,        "D",      right);
            DrawKey(x0,                    y0 + 2 * (k + g),    k * 1.5f, "CTRL",   _hudInput.Crouch);
            DrawKey(x0 + k * 1.5f + g * 2, y0 + 2 * (k + g),    k * 2.5f, "ESPACE", _hudInput.Jump);
            DrawKey(x0 + k * 4f + g * 3,   y0 + 2 * (k + g),    k,        "E",      _hudInput.Interact);
            DrawKey(x0,                    y0 + 3 * (k + g),    k * 1.5f, "SPRINT", _hudInput.Sprint);
            DrawKey(x0 + k * 1.5f + g * 2, y0 + 3 * (k + g),    k,        "R",      _hudInput.Respawn);

            string mode = _paused ? "PAUSE" : _playing ? "PLAY" : _recording ? "REC" : "IDLE";
            string frames = _playing ? (_playIndex + " / " + _frames.Count) : Time.frameCount.ToString();
            GUI.color = _recording ? Color.red : _playing ? Color.green : _paused ? Color.yellow : Color.white;
            GUI.Label(new Rect(x0, y0 + 3 * (k + g) + 52, 330, 22), "[" + mode + "]  frame " + frames);
            GUI.color = Color.white;

            // Panneau gauche : position / vitesse
            DrawRect(new Rect(10, 10, 300, 110), new Color(0f, 0f, 0f, 0.55f));
            PlayerMovementCC mover = FindMover();
            Vector3 p = mover != null ? mover.transform.position : Vector3.zero;
            CharacterController cc = mover != null ? mover.GetComponent<CharacterController>() : null;
            Vector3 v = cc != null ? cc.velocity : Vector3.zero;
            float hSpeed = new Vector2(v.x, v.z).magnitude;

            GUI.color = Color.white;
            GUI.Label(new Rect(20, 16, 290, 20), $"Pos  {p.x:0.00}  {p.y:0.00}  {p.z:0.00}");
            GUI.Label(new Rect(20, 36, 290, 20), $"Vit  {hSpeed:0.00} m/s   (Y {v.y:0.00})");
            GUI.Label(new Rect(20, 56, 290, 20), $"Dist {_distance:0.0} m   FPS {_fpsSmoothed:0}");
            GUI.Label(new Rect(20, 76, 290, 20), $"Scène {SceneManager.GetActiveScene().name}");
            GUI.color = _analysisWriter != null ? Color.red : new Color(0.6f, 0.6f, 0.6f);
            GUI.Label(new Rect(20, 96, 290, 20), _analysisWriter != null ? "● ANALYSE EN COURS" : "○ analyse arrêtée");
            GUI.color = Color.white;

            DrawTrail();
        }

        private void DrawRect(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = Color.white;
        }

        // Touche : vert si enfoncée, gris sinon, libellé centré (sans GUIStyle ni TextAnchor)
        private void DrawKey(float x, float y, float w, string label, bool pressed)
        {
            Rect r = new Rect(x, y, w, 46f);
            DrawRect(r, pressed ? new Color(0.2f, 0.9f, 0.3f, 0.95f) : new Color(0.25f, 0.25f, 0.25f, 0.9f));
            DrawRect(new Rect(r.x, r.yMax - 3, r.width, 3), new Color(0f, 0f, 0f, 0.6f));

            // Centrage approximatif : environ 8 px par caractère pour la police par défaut
            float textWidth = label.Length * 8f;
            float tx = r.x + (r.width - textWidth) * 0.5f;
            float ty = r.y + (r.height - 18f) * 0.5f;

            GUI.color = pressed ? Color.black : Color.white;
            GUI.Label(new Rect(tx, ty, textWidth + 10, 20), label);
            GUI.color = Color.white;
        }

        // =====================================================================
        // Types
        // =====================================================================
        internal sealed class SaveSlot
        {
            public PlayerSnapshot Snap;
            public int PlayIndex;
            public long TasFrame;
        }

        internal sealed class FrameInput
        {
            public Vector2 Move;
            public bool Jump, Crouch, Interact, Sprint, Respawn, Click;
            public Vector2 Look;   // delta souris (pixels) sur la frame
            // Caméra et position de référence (optionnelles : absentes des anciens fichiers).
            public bool HasCam, HasAxis, HasPos;
            public float Yaw, Pitch, AxisH, AxisV;
            public Vector3 Pos;

            public FrameInput Clone() { return (FrameInput)MemberwiseClone(); }

            public string ToLine()
            {
                var flags = new List<string>();
                if (Jump) flags.Add("jump");
                if (Crouch) flags.Add("crouch");
                if (Interact) flags.Add("interact");
                if (Sprint) flags.Add("sprint");
                if (Respawn) flags.Add("respawn");
                if (Click) flags.Add("click");
                if (Look != Vector2.zero)
                    flags.Add("look:" + Look.x.ToString("0.###", Inv) + ":" + Look.y.ToString("0.###", Inv));
                if (HasCam)
                {
                    string cam = "cam=" + Yaw.ToString("0.######", Inv) + "/" + Pitch.ToString("0.######", Inv);
                    if (HasAxis) cam += "/" + AxisH.ToString("0.######", Inv) + "/" + AxisV.ToString("0.######", Inv);
                    flags.Add(cam);
                }
                if (HasPos)
                    flags.Add("pos=" + Pos.x.ToString("0.######", Inv) + "/" + Pos.y.ToString("0.######", Inv) + "/" + Pos.z.ToString("0.######", Inv));
                return Move.x.ToString("0.###", Inv) + " " + Move.y.ToString("0.###", Inv) +
                       (flags.Count == 0 ? "" : " " + string.Join(" ", flags));
            }

            public static FrameInput Parse(string line)
            {
                string[] t = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 2) throw new FormatException("attendu: x y [actions]");
                var f = new FrameInput();
                f.Move = new Vector2(float.Parse(t[0], Inv), float.Parse(t[1], Inv));
                f.Move = Vector2.ClampMagnitude(f.Move, 1f);
                for (int i = 2; i < t.Length; i++)
                {
                    string flag = t[i].Trim().ToLowerInvariant();
                    if (flag == "jump") f.Jump = true;
                    else if (flag == "crouch") f.Crouch = true;
                    else if (flag == "interact") f.Interact = true;
                    else if (flag == "sprint") f.Sprint = true;
                    else if (flag == "respawn") f.Respawn = true;
                    else if (flag == "click") f.Click = true;
                    else if (flag.StartsWith("look:"))
                    {
                        string[] c = flag.Substring(5).Split(':');
                        if (c.Length != 2) throw new FormatException("look: attend dx:dy");
                        f.Look = new Vector2(float.Parse(c[0], Inv), float.Parse(c[1], Inv));
                    }
                    else if (flag.StartsWith("cam="))
                    {
                        string[] c = flag.Substring(4).Split('/');
                        if (c.Length != 2 && c.Length != 4) throw new FormatException("cam= attend yaw/pitch[/axeH/axeV]");
                        f.HasCam = true;
                        f.Yaw = float.Parse(c[0], Inv);
                        f.Pitch = float.Parse(c[1], Inv);
                        if (c.Length == 4)
                        {
                            f.HasAxis = true;
                            f.AxisH = float.Parse(c[2], Inv);
                            f.AxisV = float.Parse(c[3], Inv);
                        }
                    }
                    else if (flag.StartsWith("pos="))
                    {
                        string[] c = flag.Substring(4).Split('/');
                        if (c.Length != 3) throw new FormatException("pos= attend x/y/z");
                        f.HasPos = true;
                        f.Pos = new Vector3(float.Parse(c[0], Inv), float.Parse(c[1], Inv), float.Parse(c[2], Inv));
                    }
                    else throw new FormatException("action inconnue: " + flag);
                }
                return f;
            }
        }

        internal sealed class PlayerSnapshot
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public UnityEngine.Random.State RandomState;
            public string Scene = "";
            public FrameInput Cam = new FrameInput();
            public string RespawnPath = "";
            private readonly Dictionary<string, object> MoverFields = new Dictionary<string, object>();
            private readonly Dictionary<string, object> RespawnFields = new Dictionary<string, object>();

            private static bool Storable(Type t)
            {
                return t.IsPrimitive || t.IsEnum || t == typeof(Vector2) || t == typeof(Vector3) ||
                       t == typeof(Quaternion) || t == typeof(Movement) || t == typeof(LayerMask);
            }

            public static PlayerSnapshot Capture(PlayerMovementCC mover, PlayerRespawn respawn)
            {
                var s = new PlayerSnapshot
                {
                    Position = mover.transform.position,
                    Rotation = mover.transform.rotation,
                    RandomState = UnityEngine.Random.state
                };
                CaptureFields(mover, s.MoverFields);
                if (respawn != null)
                {
                    CaptureFields(respawn, s.RespawnFields);
                    s.RespawnPath = PathOf(respawn.currentRespawnPoint);
                }
                return s;
            }

            private static void CaptureFields(object obj, Dictionary<string, object> into)
            {
                foreach (FieldInfo f in obj.GetType().GetFields(InstanceFlags))
                    if (Storable(f.FieldType)) into[f.Name] = f.GetValue(obj);
                if (obj is PlayerRespawn pr) into["#currentRespawnPoint"] = pr.currentRespawnPoint;
            }

            private static void RestoreFields(object obj, Dictionary<string, object> values)
            {
                foreach (var pair in values)
                {
                    if (pair.Key == "#currentRespawnPoint")
                    {
                        Transform tr = pair.Value as Transform;
                        if (tr != null) ((PlayerRespawn)obj).currentRespawnPoint = tr;
                        continue;
                    }
                    FieldInfo f = obj.GetType().GetField(pair.Key, InstanceFlags);
                    if (f != null && !f.IsInitOnly && f.FieldType.IsInstanceOfType(pair.Value)) f.SetValue(obj, pair.Value);
                }
            }

            public void Restore(PlayerMovementCC mover, PlayerRespawn respawn)
            {
                CharacterController cc = mover.GetComponent<CharacterController>();
                bool wasEnabled = cc != null && cc.enabled;
                if (cc != null) cc.enabled = false;
                mover.transform.SetPositionAndRotation(Position, Rotation);
                RestoreFields(mover, MoverFields);
                if (respawn != null) RestoreFields(respawn, RespawnFields);
                if (cc != null) cc.enabled = wasEnabled;
                UnityEngine.Random.state = RandomState;
            }

            // ---------- Sérialisation disque ----------
            private static string PathOf(Transform t)
            {
                if (t == null) return "";
                string s = t.name;
                while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
                return s;
            }

            private static Transform Resolve(string path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                GameObject go = GameObject.Find("/" + path);
                return go != null ? go.transform : null;
            }

            private static string J(params float[] v) { return string.Join(",", v.Select(x => x.ToString("R", Inv))); }

            private static string EncodeScalar(object v)
            {
                if (v is bool) return (bool)v ? "1" : "0";
                if (v is float) return ((float)v).ToString("R", Inv);
                if (v is double) return ((double)v).ToString("R", Inv);
                if (v is Enum) return Convert.ToInt64(v).ToString(Inv);
                if (v is IConvertible) return ((IConvertible)v).ToString(Inv);
                return null;
            }

            private static object DecodeScalar(Type t, string s)
            {
                if (t == typeof(bool)) return s == "1";
                if (t == typeof(float)) return float.Parse(s, Inv);
                if (t == typeof(double)) return double.Parse(s, Inv);
                if (t.IsEnum) return Enum.ToObject(t, long.Parse(s, Inv));
                return Convert.ChangeType(s, t, Inv);
            }

            private static string Encode(object v)
            {
                if (v is Vector2) { var x = (Vector2)v; return J(x.x, x.y); }
                if (v is Vector3) { var x = (Vector3)v; return J(x.x, x.y, x.z); }
                if (v is Quaternion) { var x = (Quaternion)v; return J(x.x, x.y, x.z, x.w); }
                if (v is LayerMask) return ((LayerMask)v).value.ToString(Inv);
                if (v is Movement)
                    return string.Join(";", typeof(Movement).GetFields(InstanceFlags).Select(f => EncodeScalar(f.GetValue(v)) ?? ""));
                return EncodeScalar(v);
            }

            private static object Decode(Type t, string s)
            {
                if (t == typeof(Vector2)) { var p = s.Split(','); return new Vector2(float.Parse(p[0], Inv), float.Parse(p[1], Inv)); }
                if (t == typeof(Vector3)) { var p = s.Split(','); return new Vector3(float.Parse(p[0], Inv), float.Parse(p[1], Inv), float.Parse(p[2], Inv)); }
                if (t == typeof(Quaternion)) { var p = s.Split(','); return new Quaternion(float.Parse(p[0], Inv), float.Parse(p[1], Inv), float.Parse(p[2], Inv), float.Parse(p[3], Inv)); }
                if (t == typeof(LayerMask)) { var m = new LayerMask(); m.value = int.Parse(s, Inv); return m; }
                if (t == typeof(Movement))
                {
                    object boxed = Activator.CreateInstance(t);
                    FieldInfo[] fs = t.GetFields(InstanceFlags);
                    string[] parts = s.Split(';');
                    for (int i = 0; i < fs.Length && i < parts.Length; i++)
                        if (parts[i].Length > 0) fs[i].SetValue(boxed, DecodeScalar(fs[i].FieldType, parts[i]));
                    return boxed;
                }
                return DecodeScalar(t, s);
            }

            private static void WriteFields(StringBuilder sb, string tag, Dictionary<string, object> fields)
            {
                foreach (var pair in fields)
                {
                    if (pair.Key.StartsWith("#") || pair.Value == null) continue;
                    string e = Encode(pair.Value);
                    if (e != null) sb.Append(tag).Append(' ').Append(pair.Key).Append(' ').AppendLine(e);
                }
            }

            public string ToText(int playIndex, long tasFrame)
            {
                var sb = new StringBuilder();
                sb.AppendLine("TASSTATE 1");
                sb.Append("scene ").AppendLine(Scene);
                sb.Append("playIndex ").AppendLine(playIndex.ToString(Inv));
                sb.Append("tasFrame ").AppendLine(tasFrame.ToString(Inv));
                sb.Append("pos ").AppendLine(Encode(Position));
                sb.Append("rot ").AppendLine(Encode(Rotation));
                sb.Append("rng ").AppendLine(JsonUtility.ToJson(RandomState));
                sb.Append("rp ").AppendLine(RespawnPath);
                if (Cam != null && Cam.HasCam)
                    sb.Append("cam ").AppendLine(Cam.ToLine().Split(' ').First(x => x.StartsWith("cam=")));
                WriteFields(sb, "M", MoverFields);
                WriteFields(sb, "R", RespawnFields);
                return sb.ToString();
            }

            public static PlayerSnapshot FromText(string text, out int playIndex, out long tasFrame)
            {
                playIndex = 0; tasFrame = 0;
                var s = new PlayerSnapshot();
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0 || !lines[0].StartsWith("TASSTATE")) throw new FormatException("fichier de savestate invalide");
                foreach (string line in lines.Skip(1))
                {
                    int sp = line.IndexOf(' ');
                    string key = sp < 0 ? line : line.Substring(0, sp);
                    string val = sp < 0 ? "" : line.Substring(sp + 1);
                    switch (key)
                    {
                        case "scene": s.Scene = val; break;
                        case "playIndex": playIndex = int.Parse(val, Inv); break;
                        case "tasFrame": tasFrame = long.Parse(val, Inv); break;
                        case "pos": s.Position = (Vector3)Decode(typeof(Vector3), val); break;
                        case "rot": s.Rotation = (Quaternion)Decode(typeof(Quaternion), val); break;
                        case "rng": s.RandomState = JsonUtility.FromJson<UnityEngine.Random.State>(val); break;
                        case "rp":
                            s.RespawnPath = val;
                            Transform tr = Resolve(val);
                            if (tr != null) s.RespawnFields["#currentRespawnPoint"] = tr;
                            break;
                        case "cam":
                            s.Cam = FrameInput.Parse("0 0 " + val);
                            break;
                        case "M":
                        case "R":
                        {
                            int sp2 = val.IndexOf(' ');
                            if (sp2 < 0) break;
                            string fname = val.Substring(0, sp2);
                            Type owner = key == "M" ? typeof(PlayerMovementCC) : typeof(PlayerRespawn);
                            FieldInfo fi = owner.GetField(fname, InstanceFlags);
                            if (fi == null) break;
                            try { (key == "M" ? s.MoverFields : s.RespawnFields)[fname] = Decode(fi.FieldType, val.Substring(sp2 + 1)); }
                            catch (Exception) { }
                            break;
                        }
                    }
                }
                return s;
            }
        }
    }
}
