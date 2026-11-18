# Mutes the sound of every game launched from a test copy (a path under C:\Games\StS2-*), through Windows' per-application
# volume (the mixer's mute): some Workshop mods play their own files straight through FMOD (CardVoiceMod's voices), which
# headless sessions and the game's volume settings don't silence. Never touches the Steam install's game (another path).
# Runs until stopped (or -Once). Usage: powershell -File tools/autotest/mute-copies.ps1 [-Once] [-Pattern 'C:\Games\StS2-*']
param([switch]$Once, [string]$Pattern = 'C:\Games\StS2-*', [int]$EverySec = 2)
$ErrorActionPreference = "Stop"
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BdMute {
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2 {
        int GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
        int GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl2 session);
    }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2 {
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(string name, ref Guid context);
        int GetIconPath(out IntPtr path);
        int SetIconPath(string path, ref Guid context);
        int GetGroupingParam(out Guid param);
        int SetGroupingParam(ref Guid param, ref Guid context);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
        int GetSessionIdentifier(out IntPtr id);
        int GetSessionInstanceIdentifier(out IntPtr id);
        int GetProcessId(out uint pid);
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume {
        int SetMasterVolume(float level, ref Guid context);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, ref Guid context);
        int GetMute(out bool mute);
    }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    public static class Mixer {
        // Mutes the audio sessions of the given process ids on every active output device; returns the pids newly muted.
        public static List<uint> Mute(HashSet<uint> pids) {
            var done = new List<uint>();
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDeviceCollection devices;
            en.EnumAudioEndpoints(0, 1, out devices);
            int n;
            devices.GetCount(out n);
            Guid iid = typeof(IAudioSessionManager2).GUID;
            Guid ctx = Guid.Empty;
            for (int i = 0; i < n; i++) {
                IMMDevice dev;
                devices.Item(i, out dev);
                object o;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) continue;
                var mgr = (IAudioSessionManager2)o;
                IAudioSessionEnumerator sessions;
                mgr.GetSessionEnumerator(out sessions);
                int count;
                sessions.GetCount(out count);
                for (int k = 0; k < count; k++) {
                    IAudioSessionControl2 s;
                    sessions.GetSession(k, out s);
                    uint pid;
                    s.GetProcessId(out pid);
                    if (!pids.Contains(pid)) continue;
                    var vol = (ISimpleAudioVolume)s;
                    bool muted;
                    vol.GetMute(out muted);
                    if (!muted) { vol.SetMute(true, ref ctx); done.Add(pid); }
                }
            }
            return done;
        }
    }
}
'@
do {
    $pids = New-Object 'System.Collections.Generic.HashSet[uint32]'
    foreach ($p in @(Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { }
        if ($path -and $path -like "$Pattern*" -and $path -notmatch 'steamapps') { [void]$pids.Add([uint32]$p.Id) }
    }
    if ($pids.Count -gt 0) {
        try {
            foreach ($m in [BdMute.Mixer]::Mute($pids)) { "$(Get-Date -Format HH:mm:ss) muted pid $m" }
        } catch { "$(Get-Date -Format HH:mm:ss) mixer error: $($_.Exception.Message)" }
    }
    if (-not $Once) { Start-Sleep $EverySec }
} while (-not $Once)
