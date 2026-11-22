using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class UnityPLCConnectBridge : MonoBehaviour
{
    private const int HEADER_SIZE = 128;

    // PLCSimUnity Header Offsets
    private const int OFFSET_STATUS_BYTE = 0;         // Byte: PLCSIM connection status (Read-Only)
    private const int OFFSET_PLC_STATE = 1;           // Byte: PLC operating state (Read-Only)
    private const int OFFSET_HEARTBEAT = 2;           // Byte: PLCSimUnity rolling heartbeat (Read-Only)
    private const int OFFSET_UNITY_TICK = 100;        // Int32: Unity Environment.TickCount heartbeat (Read by DashboardForm)

    // Process Image I/O Buffers
    private const int OFFSET_INPUTS = 128;            // Unity -> PLCSIM (%I)
    private const int OFFSET_OUTPUTS = 192;           // PLCSIM -> Unity (%Q)

    [Serializable]
    public class TagMapping
    {
        public string tagName;
        public bool isOutput;
        public int byteOffset;
        [Range(0, 7)] public int bitIndex;
        public bool currentValue;
    }

    [Header("PLC Connection Settings")]
    public string instanceName = "Project2";
    public string shmName = "PLCSimUnity_SharedMemory";
    public string customExePath = "";
    public int inputByteCount = 64;
    public int outputByteCount = 64;
    public float serviceStartDelay = 0.5f;
    public bool autoConnectPlcInstance = true;

    [Header("Status Diagnostics")]
    public bool serviceExeFound;
    public bool serviceAlive;
    public byte statusByte;
    public byte plcOperatingState;
    public byte serviceHeartbeat;
    public byte outputByte0_Q0;
    public bool isConnected;
    public bool simulationStarted;

    [Header("Tag Mapping Configuration")]
    public List<TagMapping> tagMappings = new List<TagMapping>();

    public bool IsConnected => isConnected && serviceAlive;
    public byte[] InputBuffer => _inputBuffer;
    public byte[] OutputBuffer => _outputBuffer;

    private MemoryMappedFile _mmf;
    private MemoryMappedViewAccessor _accessor;
    private byte[] _headerBuffer = new byte[HEADER_SIZE];
    private byte[] _inputBuffer;
    private byte[] _outputBuffer;

    private string _resolvedExePath;
    private byte _lastServiceHeartbeat;
    private byte _lastLoggedQ0;
    private float _lastHeartbeatTime;
    private float _nextReconnectAttemptTime;
    private float _nextAutoConnectAttemptTime;
    private bool _loggedPrivilegeError;
    private bool _loggedConnectionSuccess;
    private bool _loggedPlcRunAttached;
    private bool _loggedExecutableNotFound;

    private void Awake()
    {
        EnsureBuffers();
        ResolveExecutablePath();
    }

    private void OnEnable()
    {
        EnsureBuffers();
        ResolveExecutablePath();

        if (!IsServiceProcessRunning() && serviceExeFound)
        {
            TryLaunchCouplerService();
            _nextReconnectAttemptTime = Time.unscaledTime + Mathf.Max(0.1f, serviceStartDelay);
        }
        else
        {
            TryOpenSharedMemory();
        }
    }

    private void Update()
    {
        SyncSharedMemory();
    }

    private void OnDisable()
    {
        DisconnectAndCleanup();
    }

    private void OnDestroy()
    {
        DisconnectAndCleanup();
    }

    public void BeginSimulation()
    {
        simulationStarted = true;
        EnsureBuffers();

        if (_accessor == null)
        {
            ResolveExecutablePath();
            if (!IsServiceProcessRunning() && serviceExeFound)
                TryLaunchCouplerService();
            else
                TryOpenSharedMemory();
        }

        if (!isConnected && autoConnectPlcInstance)
            TryTriggerCouplerAutoConnect(forceImmediate: true);

        Debug.Log("[UnityPLCConnectBridge] Simulation started.");
    }

    private void EnsureBuffers()
    {
        int inSize = Mathf.Max(1, inputByteCount);
        int outSize = Mathf.Max(1, outputByteCount);

        if (_inputBuffer == null || _inputBuffer.Length != inSize)
            _inputBuffer = new byte[inSize];

        if (_outputBuffer == null || _outputBuffer.Length != outSize)
            _outputBuffer = new byte[outSize];
    }

    private void ResolveExecutablePath()
    {
        _resolvedExePath = string.Empty;
        serviceExeFound = false;

        try
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            var candidates = new List<string>();
            string configuredPath = customExePath == null ? string.Empty : customExePath.Trim().Trim('"').Trim();

            if (!string.IsNullOrEmpty(configuredPath))
                candidates.Add(Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(projectRoot ?? Application.dataPath, configuredPath));

            if (!string.IsNullOrEmpty(projectRoot))
            {
                candidates.Add(Path.Combine(projectRoot, "PLCSimUnity", "PLCSimUnity.exe"));
                candidates.Add(Path.Combine(projectRoot, "UnityPLCConnect", "PLCSimUnity.exe"));
                candidates.Add(Path.Combine(projectRoot, "PLCSimUnity.exe"));
            }

            foreach (string candidate in candidates)
            {
                string path = candidate;
                if (Directory.Exists(path))
                    path = Path.Combine(path, "PLCSimUnity.exe");
                else if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    path += ".exe";

                if (!File.Exists(path))
                    continue;

                _resolvedExePath = Path.GetFullPath(path);
                serviceExeFound = true;
                _loggedExecutableNotFound = false;
                return;
            }

            if (!_loggedExecutableNotFound)
            {
                Debug.LogWarning(
                    "[UnityPLCConnectBridge] PLCSimUnity.exe was not found. Set Custom Exe Path or place it " +
                    "in the project-root PLCSimUnity or UnityPLCConnect folder.");
                _loggedExecutableNotFound = true;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[UnityPLCConnectBridge] Could not resolve PLCSimUnity.exe: {ex.Message}");
        }
    }

    private bool IsServiceProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("PLCSimUnity").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private void TryLaunchCouplerService()
    {
        if (!serviceExeFound || string.IsNullOrEmpty(_resolvedExePath))
            return;

        try
        {
            string args = string.IsNullOrWhiteSpace(instanceName)
                ? "--auto-connect"
                : $"--instance \"{instanceName.Trim()}\" --auto-connect";

            var startInfo = new ProcessStartInfo
            {
                FileName = _resolvedExePath,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(_resolvedExePath) ?? string.Empty,
                UseShellExecute = true
            };
            Process.Start(startInfo);
            _nextAutoConnectAttemptTime = Time.unscaledTime + 1.5f;
            Debug.Log($"[UnityPLCConnectBridge] Launched coupler service: {_resolvedExePath} {args}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[UnityPLCConnectBridge] Could not auto-start PLCSimUnity.exe ({ex.Message}).");
        }
    }

    private bool TryOpenSharedMemory()
    {
        if (_accessor != null && _mmf != null)
            return true;

        try
        {
            int totalRequiredSize = OFFSET_OUTPUTS + Mathf.Max(1, outputByteCount);
            _mmf = MemoryMappedFile.OpenExisting(shmName, MemoryMappedFileRights.ReadWrite);
            _accessor = _mmf.CreateViewAccessor(0, totalRequiredSize, MemoryMappedFileAccess.ReadWrite);

            _loggedPrivilegeError = false;
            _lastHeartbeatTime = Time.unscaledTime;

            if (!_loggedConnectionSuccess)
            {
                Debug.Log($"[UnityPLCConnectBridge] Shared Memory Connected: '{shmName}'");
                _loggedConnectionSuccess = true;
            }

            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            if (!_loggedPrivilegeError)
            {
                Debug.LogError(
                    "[UnityPLCConnectBridge] Access denied opening shared memory. " +
                    "Launch Unity Hub / Unity Editor as Administrator to match PLCSimUnity.exe.");
                _loggedPrivilegeError = true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[UnityPLCConnectBridge] Shared memory open failed: {ex.Message}");
            return false;
        }
    }

    private void SyncSharedMemory()
    {
        EnsureBuffers();

        if (_accessor == null)
        {
            serviceAlive = false;
            isConnected = false;

            if (Time.unscaledTime >= _nextReconnectAttemptTime)
            {
                _nextReconnectAttemptTime = Time.unscaledTime + 1.0f;
                TryOpenSharedMemory();
            }
            return;
        }

        try
        {
            // 1. Write Unity Environment.TickCount at offset 100 for DashboardForm telemetry
            int unityTick = Environment.TickCount;
            if (unityTick == 0) unityTick = 1;
            _accessor.Write(OFFSET_UNITY_TICK, unityTick);

            // 2. Read Header Status (0..127 owned by PLCSimUnity)
            _accessor.ReadArray(0, _headerBuffer, 0, HEADER_SIZE);

            statusByte = _headerBuffer[OFFSET_STATUS_BYTE];
            plcOperatingState = _headerBuffer[OFFSET_PLC_STATE];
            serviceHeartbeat = _headerBuffer[OFFSET_HEARTBEAT];

            if (serviceHeartbeat != _lastServiceHeartbeat)
            {
                _lastServiceHeartbeat = serviceHeartbeat;
                _lastHeartbeatTime = Time.unscaledTime;
            }

            bool heartbeatFresh = (Time.unscaledTime - _lastHeartbeatTime) < 2.5f;
            serviceAlive = heartbeatFresh || IsServiceProcessRunning();

            // 3. Exchange Process Image I/O (%I written at 128, %Q read at 192)
            _accessor.WriteArray(OFFSET_INPUTS, _inputBuffer, 0, _inputBuffer.Length);
            _accessor.ReadArray(OFFSET_OUTPUTS, _outputBuffer, 0, _outputBuffer.Length);

            outputByte0_Q0 = _outputBuffer[0];
            if (outputByte0_Q0 != _lastLoggedQ0)
            {
                _lastLoggedQ0 = outputByte0_Q0;
                Debug.Log($"[UnityPLCConnectBridge] %Q0 changed -> 0x{outputByte0_Q0:X2} (Q0.0={(outputByte0_Q0 & 1) != 0}, Q0.1={(outputByte0_Q0 & 2) != 0})");
            }

            UpdateTagMappings();

            bool plcRunning = statusByte >= 4 || plcOperatingState == 5 || plcOperatingState == 8;
            isConnected = serviceAlive && plcRunning;

            if (isConnected)
            {
                if (!_loggedPlcRunAttached)
                {
                    Debug.Log($"[UnityPLCConnectBridge] Attached to PLC instance '{instanceName}' (StatusByte={statusByte}, OperatingState={plcOperatingState}).");
                    _loggedPlcRunAttached = true;
                }
            }
            else
            {
                _loggedPlcRunAttached = false;
                if (serviceAlive && autoConnectPlcInstance)
                    TryTriggerCouplerAutoConnect(forceImmediate: false);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[UnityPLCConnectBridge] Shared memory sync interrupted: {ex.Message}");
            DisconnectAndCleanup();
            _nextReconnectAttemptTime = Time.unscaledTime + 1.0f;
        }
    }

    private void TryTriggerCouplerAutoConnect(bool forceImmediate)
    {
        if (!forceImmediate && Time.unscaledTime < _nextAutoConnectAttemptTime)
            return;

        _nextAutoConnectAttemptTime = Time.unscaledTime + 2.0f;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        try
        {
            Process[] procs = Process.GetProcessesByName("PLCSimUnity");
            for (int i = 0; i < procs.Length; i++)
            {
                IntPtr mainHwnd = procs[i].MainWindowHandle;
                if (mainHwnd == IntPtr.Zero) continue;

                IntPtr enableAutoDiscoverBtn = IntPtr.Zero;
                IntPtr connectSelectedBtn = IntPtr.Zero;

                EnumChildWindows(mainHwnd, (hwnd, lParam) =>
                {
                    string text = GetControlText(hwnd);
                    if (string.IsNullOrEmpty(text))
                        return true;

                    if (text.StartsWith("Enable Auto-Discover", StringComparison.OrdinalIgnoreCase))
                        enableAutoDiscoverBtn = hwnd;
                    else if (text.IndexOf("Connect to Selected Instance", StringComparison.OrdinalIgnoreCase) >= 0)
                        connectSelectedBtn = hwnd;

                    return true;
                }, IntPtr.Zero);

                if (enableAutoDiscoverBtn != IntPtr.Zero && IsWindowEnabled(enableAutoDiscoverBtn))
                {
                    SendMessage(enableAutoDiscoverBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    Debug.Log($"[UnityPLCConnectBridge] Triggered 'Enable Auto-Discover Mode' on PLCSimUnity for '{instanceName}'.");
                    return;
                }

                if (connectSelectedBtn != IntPtr.Zero && IsWindowEnabled(connectSelectedBtn))
                {
                    SendMessage(connectSelectedBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    Debug.Log($"[UnityPLCConnectBridge] Triggered 'Connect to Selected Instance' on PLCSimUnity for '{instanceName}'.");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[UnityPLCConnectBridge] Auto-connect trigger skipped: {ex.Message}");
        }
#endif
    }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
    private const uint BM_CLICK = 0x00F5;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private static string GetControlText(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        int len = GetWindowText(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }
#endif

    private void UpdateTagMappings()
    {
        if (tagMappings == null || tagMappings.Count == 0)
            return;

        for (int i = 0; i < tagMappings.Count; i++)
        {
            TagMapping mapping = tagMappings[i];
            if (mapping == null) continue;

            if (mapping.isOutput)
                mapping.currentValue = ReadOutputBit(mapping.byteOffset, mapping.bitIndex);
            else
                WriteInputBit(mapping.byteOffset, mapping.bitIndex, mapping.currentValue);
        }
    }

    private void DisconnectAndCleanup()
    {
        if (_accessor != null)
        {
            try
            {
                _accessor.Write(OFFSET_UNITY_TICK, 0);
                _accessor.Flush();
            }
            catch { }

            _accessor.Dispose();
            _accessor = null;
        }

        if (_mmf != null)
        {
            _mmf.Dispose();
            _mmf = null;
        }

        serviceAlive = false;
        isConnected = false;
        simulationStarted = false;
        _loggedConnectionSuccess = false;
        _loggedPlcRunAttached = false;
    }

    // =========================================================================
    // Public I/O API for RealVirtualPLCSimAdapter
    // =========================================================================

    public bool ReadOutputBit(int byteOffset, int bitIndex)
    {
        if (_outputBuffer == null || byteOffset < 0 || byteOffset >= _outputBuffer.Length)
            return false;
        if (bitIndex < 0 || bitIndex > 7)
            return false;

        return (_outputBuffer[byteOffset] & (1 << bitIndex)) != 0;
    }

    public bool GetOutputBit(int byteOffset, int bitIndex) => ReadOutputBit(byteOffset, bitIndex);

    public void WriteInputBit(int byteOffset, int bitIndex, bool value)
    {
        if (_inputBuffer == null || byteOffset < 0 || byteOffset >= _inputBuffer.Length)
            return;
        if (bitIndex < 0 || bitIndex > 7)
            return;

        if (value)
            _inputBuffer[byteOffset] |= (byte)(1 << bitIndex);
        else
            _inputBuffer[byteOffset] &= (byte)~(1 << bitIndex);
    }

    public void SetInputBit(int byteOffset, int bitIndex, bool value) => WriteInputBit(byteOffset, bitIndex, value);

    public byte ReadOutputByte(int byteOffset)
    {
        if (_outputBuffer == null || byteOffset < 0 || byteOffset >= _outputBuffer.Length)
            return 0;
        return _outputBuffer[byteOffset];
    }

    public void WriteInputByte(int byteOffset, byte value)
    {
        if (_inputBuffer == null || byteOffset < 0 || byteOffset >= _inputBuffer.Length)
            return;
        _inputBuffer[byteOffset] = value;
    }

    public float ReadOutputFloat(int byteOffset)
    {
        if (_outputBuffer == null || byteOffset < 0 || byteOffset + 3 >= _outputBuffer.Length)
            return 0f;

        byte[] bytes =
        {
            _outputBuffer[byteOffset + 3],
            _outputBuffer[byteOffset + 2],
            _outputBuffer[byteOffset + 1],
            _outputBuffer[byteOffset]
        };
        return BitConverter.ToSingle(bytes, 0);
    }

    public void WriteInputFloat(int byteOffset, float value)
    {
        if (_inputBuffer == null || byteOffset < 0 || byteOffset + 3 >= _inputBuffer.Length)
            return;

        byte[] bytes = BitConverter.GetBytes(value);
        _inputBuffer[byteOffset] = bytes[3];
        _inputBuffer[byteOffset + 1] = bytes[2];
        _inputBuffer[byteOffset + 2] = bytes[1];
        _inputBuffer[byteOffset + 3] = bytes[0];
    }
}