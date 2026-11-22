# Bottling Industry Digital Twin

## PLCSimUnity startup

`UnityPLCConnectBridge` attempts to start `PLCSimUnity.exe` when its GameObject is enabled (normally when entering Play mode). It first checks the component's **Custom Exe Path**, then searches these locations relative to the project root (or the standalone build folder):

1. `PLCSimUnity\PLCSimUnity.exe`
2. `UnityPLCConnect\PLCSimUnity.exe`
3. `PLCSimUnity.exe`

The `SampleScene` leaves **Custom Exe Path** empty so it can use the project-relative location instead of a developer-specific absolute path. If your executable is elsewhere, set **Custom Exe Path** to its full path or a path relative to the project root. A directory can be specified instead of the executable filename.

For Windows standalone builds, the post-build step copies the repository's `PLCSimUnity` folder alongside the build executable, so the first location above is used automatically.

Starting the service does not by itself guarantee a PLC connection. Siemens PLCSIM Advanced and the target virtual PLC instance must be available and running. If startup fails, check Unity's Console for the bridge's missing-executable or process-start warning. If Windows blocks the launch or shared-memory access, run Unity and PLCSimUnity with matching privileges (for example, both elevated). The startup checklist in the scene reports the remaining service and PLC connection checks.
