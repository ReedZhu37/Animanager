"""Import Animanager transfers and add matching Cascadeur AutoPosing rigs."""

import json
import os
import re
import time
from pathlib import Path

import csc
import events

VERSION = 3
RIG_TEMPLATES = {"UE4 Mannequin": "UE4_New.qrigcasc", "UE5 Manny / Quinn": "UE5.qrigcasc"}


def name():
    return "Animanager.Toggle connection"


def command_name():
    return name()


def description():
    return "Start or stop receiving animation transfers from Animanager."


def run(scene):
    root = Path(os.environ["LOCALAPPDATA"]) / "Animanager" / "Plugins" / "Cascadeur" / f"v{VERSION}"
    receiver = getattr(events, "_animanager_receiver", None)
    if receiver is not None:
        receiver.stop()
        del events._animanager_receiver
        if receiver.root == root:
            scene.info("Animanager receiver stopped.")
            return

    receiver = Receiver(root)
    receiver.start()
    events._animanager_receiver = receiver
    scene.info("Animanager receiver connected.")


class Receiver:
    def __init__(self, root):
        self.root = root
        self.ready = root / f"receiver-{os.getpid()}.ready"
        self.last_poll = float("-inf")
        self.last_heartbeat = float("-inf")
        self.importing = False

    def start(self):
        self.root.mkdir(parents=True, exist_ok=True)
        self.ready.touch()
        self.subscription = events.scene_idle_manager.subscribe(self.poll)

    def stop(self):
        events.scene_idle_manager.unsubscribe(self.subscription)
        self.ready.unlink(missing_ok=True)

    def poll(self, scene):
        now = time.monotonic()
        if self.importing or now - self.last_poll < 0.25:
            return
        self.last_poll = now
        if now - self.last_heartbeat >= 2:
            self.ready.touch()
            self.last_heartbeat = now

        for pending in (self.root / "inbox").glob("*.json"):
            if not re.fullmatch(r"[0-9a-f]{32}", pending.stem) or pending.is_symlink():
                continue
            folder = self.root / "transfers" / pending.stem
            if folder.is_symlink() or not folder.is_dir():
                continue
            claimed = folder / "processing.json"
            try:
                pending.replace(claimed)
            except FileNotFoundError:
                continue
            self.importing = True
            try:
                self.receive(claimed, scene)
            finally:
                self.importing = False
            break

    def receive(self, claimed, scene):
        try:
            request = json.loads(claimed.read_text(encoding="utf-8"))
            if request["version"] != VERSION or request["expiresUnixMilliseconds"] <= int(time.time() * 1000):
                raise ValueError("The import request expired or uses an unsupported version.")
            filename = Path(request["file"])
            source = claimed.parent / filename
            if (filename.name != request["file"] or filename.suffix.lower() != ".glb"
                    or source.is_symlink() or not source.is_file()):
                raise ValueError("The transfer must contain a GLB file in its own folder.")
            result = import_glb(source, request.get("bindPose", []))
        except Exception as error:
            result = {"imported": False, "error": str(error)}

        staged = claimed.with_name("result.tmp")
        staged.write_text(json.dumps(result), encoding="utf-8")
        staged.replace(claimed.with_name("result.json"))
        if not result["imported"]:
            scene.error("Animanager import failed: " + result["error"])


def import_glb(source, bind_pose, templates=None):
    app = csc.app.get_application()
    manager = app.get_scene_manager()
    view = manager.create_application_scene()
    manager.set_current_scene(view)
    scene = view.domain_scene()
    options = csc.glb.ImportOptions()
    options.fps = 30
    options.include_animation = True
    options.include_objects = True
    options.is_update_mode = False
    options.throw_exception = True
    options.scale_factor = 100.0
    csc.glb.process_import(scene, str(source), options)
    last_key = scene.layers_viewer().last_key_pos()
    if last_key < 1:
        raise ValueError("Cascadeur did not create animation keys beyond the first frame.")
    if templates is None:
        templates = Path(__file__).resolve().parents[3] / "autorig_templates"
    try:
        joints = csc.model.PathName.get_path_names_by_behavior("Joint", scene.model_viewer())
        rig, template = match_template(joints, templates)
        if rig != "UE4 Mannequin":
            raise ValueError("已识别 UE5 Manny / Quinn，请在 Cascadeur 中检查绑定姿态后生成 Rig。")
        poses = map_bind_pose(joints, bind_pose)
        add_autoposing(app, view, scene, template, poses, last_key)
    except Exception as error:
        scene.warning("Animanager imported the animation; AutoPosing rig: " + str(error))
        return {"imported": True, "rigIssue": str(error)}
    scene.success("Animanager imported " + source.stem + " with " + rig + " AutoPosing")
    return {"imported": True, "rig": rig}


def match_template(joints, templates):
    paths = {tuple(csc.model.get_path_without_namespace(path).full_path()) for path in joints}
    matches = []
    for rig, filename in RIG_TEMPLATES.items():
        template = templates / filename
        if not template.is_file():
            continue
        data = json.loads(template.read_text(encoding="utf-8-sig"))
        body = next(section for section in data["Document"] if section["Title"] == "Body")
        required = [tuple(bone["Joint path"]) + (bone["Joint name"],)
                    for section in body["Sections"] for bone in section["Names"] if bone["Joint name"]]
        anchor = next(path for path in required if path[-1] == "pelvis")
        for path in paths:
            if path[-len(anchor):] != anchor:
                continue
            prefix = path[:-len(anchor)]
            if all(prefix + joint in paths for joint in required):
                matches.append((rig, template))
    if len(matches) != 1:
        raise ValueError("没有找到唯一匹配的 UE4 / UE5 骨架模板。")
    return matches[0]


def map_bind_pose(joints, bind_pose):
    import numpy as np

    by_name = {}
    lookup = {}
    for path, joint in joints.items():
        names = tuple(csc.model.get_path_without_namespace(path).full_path())
        by_name.setdefault(names[-1], []).append((names, joint))

    for bound in bind_pose:
        path = tuple(name.rsplit(":", 1)[-1] for name in bound["path"])
        if not path:
            continue
        candidates = [joint for names, joint in by_name.get(path[-1], []) if names[-len(path):] == path]
        if len(candidates) == 1:
            position = np.asarray(bound["position"], dtype=np.float32)
            rotation = np.asarray(bound["rotation"], dtype=np.float32)
            if position.shape != (3,) or rotation.shape != (4,) or not np.isfinite(position).all() or not np.isfinite(rotation).all():
                raise ValueError("绑定姿态包含无效坐标。")
            x, y, z, w = map(float, rotation)
            lookup[path[-1]] = (candidates[0], position, csc.math.Rotation.from_quaternion(w, x, y, z))

    if any(name not in lookup for name in ("head", "foot_l", "foot_r", "pelvis", "hand_l", "hand_r")):
        raise ValueError("生成 AutoPosing 需要带完整绑定姿态的蒙皮网格。")
    up = lookup["head"][1] - (lookup["foot_l"][1] + lookup["foot_r"][1]) / 2
    if np.linalg.norm(up) < 0.01 or up[1] < np.linalg.norm(up) * 0.85:
        raise ValueError("绑定姿态没有沿 Cascadeur 的 +Y 轴站立，请检查发送时选择的坐标。")
    return lookup.values()


def add_autoposing(app, view, scene, template, poses, last_key):
    from rig_mode import on, off

    behaviors = scene.behaviour_viewer()
    if not any(behaviors.get_behaviour_objects_range(mesh, "linked_objects")
               for mesh in behaviors.get_behaviours("MeshObject")):
        raise ValueError("生成 AutoPosing 需要带绑定姿态的蒙皮网格。")

    on.run_raw(scene, [0.0, 0.5, 0.0])
    if not behaviors.get_behaviours("AnimationInfo"):
        raise RuntimeError("Cascadeur 未能进入 Rig Mode。")

    # Rig Mode preserves animation while these scene-space transforms set the rig's reference pose.
    def apply_bind_pose(model, update, scene_updater):
        actuals = set()
        for joint, position, rotation in poses:
            group = update.get_object_by_id(joint).root_group()
            translation = group.node_deep("Position")
            orientation = group.node_deep("Rotation")
            translation.set_value(position * 100.0, 0)
            orientation.set_value(rotation, 0)
            actuals.update((translation.data_id(), orientation.data_id()))
        scene_updater.run_update(actuals, 0)

    if not scene.modify_update("Set Animanager bind pose in scene coordinates", apply_bind_pose):
        raise RuntimeError("Cascadeur 未能设置换算后的绑定姿态。")
    rigging = app.get_tools_manager().get_tool("RiggingToolWindowTool").editor(view)
    rigging.set_is_create_autoposing(True)
    model = scene.model_viewer()
    before = len(model.get_objects())
    rigging.create_from_qrt_by_fileName(str(template))
    if len(model.get_objects()) <= before:
        raise RuntimeError("请检查 Cascadeur 的 Quick Rigging 提示，再完成 Generate Rig。")

    off.run(scene, True)
    if (behaviors.get_behaviours("AnimationInfo") or not behaviors.get_behaviours("AutoPosingLink")
            or scene.layers_viewer().last_key_pos() != last_key):
        raise RuntimeError("Cascadeur 未完成 AutoPosing Rig 和动画的恢复。")
