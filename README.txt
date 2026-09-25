Network Painter Server v2.0.0

Goal
- Server-only BepInEx plugin.
- Vanilla clients do NOT install this plugin.
- Paint one cable, pipe, or chute segment normally; server propagates that color through the connected network.
- No Shift/Ctrl modes.
- LaunchPad is not required for this DLL when it is dropped directly into BepInEx/plugins.

Why v2 is different
- Uses the current Stationeers CustomColorIndex naming found in the supplied Assembly-CSharp.dll.
- Hooks SetCustomColor and CustomColorIndex setter on the server instead of polling every Structure.
- Uses reflection for CableNetwork/PipeNetwork/ChuteNetwork and StructureList to reduce breakage from minor game updates.
- Has recursion protection so repainting the network does not trigger itself endlessly.

Install after build
1. Server must have BepInEx 5.4.x working.
2. Create: BepInEx\plugins\NetworkPainterServer\
3. Put ONLY NetworkPainterServer.dll in that folder.
4. Restart server.
5. Check BepInEx\LogOutput.log.

Expected startup lines
- Network Painter Server 2.0.0 starting
- Patched color hook: ...
- Network Painter Server ready. Patched X color method(s). Vanilla clients require no mod.

Expected paint line
- Network paint propagated to X connected structure(s).

If it starts but painting does not propagate, send BepInEx\LogOutput.log after painting one cable segment. The startup hook lines will show exactly which current Stationeers color method was found.
