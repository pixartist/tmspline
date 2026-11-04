// Camera unlock: lets the map editor's camera leave the 48x48 grid and go higher than usual. TMSpline tracks may run
// up to 2 km outside the grid (the game keeps and drives them there), and the editor's camera would stop at the edge.
//
// The editor's orbital camera keeps its position limits right after its targeted position: the lowest and highest
// X/Z (two vec2) and the height range (a vec2). We widen them once per editor session. Approach and offsets from
// Editor++ by XertroV (https://github.com/XertroV/tm-editor-plus-plus, released under the Unlicense).

[Setting category="Editor camera" name="Unlock camera" description="Let the map editor's camera move outside the map grid and higher than usual, e.g. to tracks built outside the grid."]
bool S_UnlockCamera = true;

// Offsets from m_TargetedPosition (vec3) to the camera's limits.
const uint16 CAMERA_MIN_XZ = 0x18;
const uint16 CAMERA_MAX_XZ = 0x20;
const uint16 CAMERA_HEIGHT_RANGE = 0x28;

void CameraLoop()
{
    // Where m_TargetedPosition sits in the camera, from Openplanet's reflection (so a game update moving it is followed).
    auto type = Reflection::GetType("CGameControlCameraEditorOrbital");
    if (type is null) { warn("Camera unlock not available: camera type not found"); return; }
    auto member = type.GetMember("m_TargetedPosition");
    if (member is null || member.Offset == 0xFFFF) { warn("Camera unlock not available: camera position not found"); return; }
    uint16 target = member.Offset;

    while (true) {
        if (S_UnlockCamera) UnlockEditorCamera(target);
        sleep(500);
    }
}

void UnlockEditorCamera(uint16 target)
{
    auto editor = cast<CGameCtnEditorFree>(GetApp().Editor);
    if (editor is null || editor.OrbitalCameraControl is null) return;
    auto camera = editor.OrbitalCameraControl;
    // Already widened in this editor session.
    if (Dev::GetOffsetVec2(camera, target + CAMERA_MIN_XZ).x <= -1000000) return;
    Dev::SetOffset(camera, target + CAMERA_MIN_XZ, vec2(-90000000));
    Dev::SetOffset(camera, target + CAMERA_MAX_XZ, vec2(90000000));
    Dev::SetOffset(camera, target + CAMERA_HEIGHT_RANGE, vec2(-100, 4000));
    trace("Editor camera unlocked");
}
