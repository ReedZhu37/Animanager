"""Strip UE-specific texture links, without reimporting or altering model/skin/animation data."""
from pathlib import Path
import json, hashlib, shutil
from io_scene_fbx import parse_fbx, encode_bin

folder=Path(__file__).resolve().parents[1]/'BuiltinAssets'/'Baselines'
manifest=json.loads((folder/'provenance.json').read_text(encoding='utf-8'))
methods=dict(zip('BCZYILFDRSifdlbc', ['add_bool','add_char','add_int8','add_int16','add_int32','add_int64','add_float32','add_float64','add_bytes','add_string','add_int32_array','add_float32_array','add_float64_array','add_int64_array','add_bool_array','add_byte_array']))
def encode(elem):
    out=encode_bin.FBXElem(elem.id)
    for kind,value in zip(elem.props_type,elem.props): getattr(out,methods[chr(kind)])(value)
    out.elems=[encode(c) for c in elem.elems]
    return out
def core_digest(tree):
    objects=next(c for c in tree.elems if c.id==b'Objects')
    return hashlib.sha256(repr([c for c in objects.elems if c.id not in (b'Texture',b'Video',b'Material')]).encode()).hexdigest()
for record in manifest['assets']:
    path=folder/record['file'];tree,version=parse_fbx.parse(str(path))
    original_hash=hashlib.sha256(path.read_bytes()).hexdigest();before=core_digest(tree)
    objects=next(c for c in tree.elems if c.id==b'Objects')
    removed={c.props[0] for c in objects.elems if c.id in (b'Texture',b'Video')}
    objects.elems[:]=[c for c in objects.elems if c.id not in (b'Texture',b'Video')]
    connections=next(c for c in tree.elems if c.id==b'Connections')
    connections.elems[:]=[c for c in connections.elems if not any(p in removed for p in c.props[1:3])]
    temp=path.with_suffix('.clean.fbx');encode_bin.write(str(temp),encode(tree),version)
    after,_=parse_fbx.parse(str(temp));assert core_digest(after)==before,'Model data changed'
    temp.replace(path)
    record.update({'raw_export_sha256':original_hash,'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),
                   'bytes':path.stat().st_size,'removed_texture_objects':len(removed),'core_data_sha256':before})
manifest['materials']='UE texture/video objects and their connections removed; geometry, skeleton, skin, animation objects structurally verified unchanged. Neutral material appearance only.'
(folder/'provenance.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print('ANIMANAGER_BASELINE_TEXTURE_CLEANUP_OK')
