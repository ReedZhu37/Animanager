"""Generate original, redistributable skinned glTF fixtures. Python standard library only."""
import json, math, struct
from pathlib import Path

DEST = Path(__file__).resolve().parents[1] / 'BuiltinAssets' / 'Samples'
DEST.mkdir(parents=True, exist_ok=True)

def make(name, duration, amplitude, color, animated=True):
    blob = bytearray()
    views, accessors = [], []
    def accessor(values, kind, component=5126, bounds=False):
        components = {'SCALAR':1,'VEC3':3,'VEC4':4,'MAT4':16}[kind]
        while len(blob)%4: blob.append(0)
        offset = len(blob)
        fmt = 'f' if component==5126 else 'H'
        blob.extend(struct.pack('<'+fmt*len(values), *values))
        views.append({'buffer':0,'byteOffset':offset,'byteLength':len(blob)-offset})
        entry = {'bufferView':len(views)-1,'componentType':component,'count':len(values)//components,'type':kind}
        if bounds:
            entry['min']=[min(values[i::components]) for i in range(components)]
            entry['max']=[max(values[i::components]) for i in range(components)]
        accessors.append(entry)
        return len(accessors)-1

    # name, parent, local joint translation, body dimensions, body center offset
    rig = [
        ('Hips',-1,(0,1,0),(.35,.22,.25),(0,0,0)),
        ('Spine',0,(0,.16,0),(.29,.22,.22),(0,.06,0)),
        ('Chest',1,(0,.2,0),(.49,.28,.28),(0,.05,0)),
        ('Head',2,(0,.32,0),(.28,.28,.26),(0,.06,0)),
        ('LeftUpperArm',2,(.34,.1,0),(.16,.28,.18),(0,-.12,0)),
        ('LeftLowerArm',4,(0,-.28,0),(.14,.26,.16),(0,-.12,0)),
        ('LeftHand',5,(0,-.27,0),(.15,.12,.18),(0,-.04,0)),
        ('RightUpperArm',2,(-.34,.1,0),(.16,.28,.18),(0,-.12,0)),
        ('RightLowerArm',7,(0,-.28,0),(.14,.26,.16),(0,-.12,0)),
        ('RightHand',8,(0,-.27,0),(.15,.12,.18),(0,-.04,0)),
        ('LeftUpperLeg',0,(.15,-.12,0),(.19,.39,.22),(0,-.18,0)),
        ('LeftLowerLeg',10,(0,-.4,0),(.16,.36,.18),(0,-.16,0)),
        ('LeftFoot',11,(0,-.38,0),(.19,.13,.32),(0,0,.07)),
        ('RightUpperLeg',0,(-.15,-.12,0),(.19,.39,.22),(0,-.18,0)),
        ('RightLowerLeg',13,(0,-.4,0),(.16,.36,.18),(0,-.16,0)),
        ('RightFoot',14,(0,-.38,0),(.19,.13,.32),(0,0,.07)),
    ]
    worlds, nodes = [], []
    for i,(label,parent,translation,dimensions,offset) in enumerate(rig):
        worlds.append(tuple(translation[j]+(worlds[parent][j] if parent>=0 else 0) for j in range(3)))
        nodes.append({'name':label,'translation':translation})
        if parent>=0: nodes[parent].setdefault('children',[]).append(i)

    positions,normals,joints,weights,indices = [],[],[],[],[]
    def cube(center, dimensions, joint):
        # CCW faces, flat shaded vertices
        faces = [((1,0,0),[(1,-1,-1),(1,1,-1),(1,1,1),(1,-1,1)]),
                 ((-1,0,0),[(-1,-1,1),(-1,1,1),(-1,1,-1),(-1,-1,-1)]),
                 ((0,1,0),[(-1,1,-1),(-1,1,1),(1,1,1),(1,1,-1)]),
                 ((0,-1,0),[(-1,-1,1),(-1,-1,-1),(1,-1,-1),(1,-1,1)]),
                 ((0,0,1),[(-1,-1,1),(1,-1,1),(1,1,1),(-1,1,1)]),
                 ((0,0,-1),[(1,-1,-1),(-1,-1,-1),(-1,1,-1),(1,1,-1)])]
        for normal,corners in faces:
            base=len(positions)//3
            for corner in corners:
                positions.extend(center[j]+corner[j]*dimensions[j]/2 for j in range(3))
                normals.extend(normal);joints.extend((joint,0,0,0));weights.extend((1,0,0,0))
            indices.extend(base+k for k in (0,1,2,0,2,3))
    for i,(_,_,_,dims,offset) in enumerate(rig):
        cube([worlds[i][j]+offset[j] for j in range(3)],dims,i)
    primitive={'attributes':{'POSITION':accessor(positions,'VEC3',bounds=True),'NORMAL':accessor(normals,'VEC3'),
                             'JOINTS_0':accessor(joints,'VEC4',5123),'WEIGHTS_0':accessor(weights,'VEC4')},
               'indices':accessor(indices,'SCALAR',5123),'material':0}
    matrices=[]
    for x,y,z in worlds: matrices.extend((1,0,0,0,0,1,0,0,0,0,1,0,-x,-y,-z,1))
    bind=accessor(matrices,'MAT4')
    nodes.append({'name':'StudioBot','mesh':0,'skin':0})
    doc={'asset':{'version':'2.0','generator':'Animanager original sample generator'},'scene':0,
         'scenes':[{'name':name,'nodes':[0,len(rig)]}],'nodes':nodes,
         'meshes':[{'name':'StudioBotMesh','primitives':[primitive]}],
         'skins':[{'name':'Humanoid','joints':list(range(len(rig))),'skeleton':0,'inverseBindMatrices':bind}],
         'materials':[{'name':'Clay','pbrMetallicRoughness':{'baseColorFactor':color+[1],'metallicFactor':.12,'roughnessFactor':.7}}]}
    if animated:
        times=[duration*i/32 for i in range(33)]
        time_accessor=accessor(times,'SCALAR',bounds=True)
        samplers,channels=[],[]
        def channel(node, path, values, kind):
            samplers.append({'input':time_accessor,'output':accessor(values,kind),'interpolation':'LINEAR'})
            channels.append({'sampler':len(samplers)-1,'target':{'node':node,'path':path}})
        for joint,phase,mult in [(4,0,-1),(7,math.pi,-1),(10,0,1),(13,math.pi,1),(11,0,-.6),(14,math.pi,-.6),(3,0,.08)]:
            quats=[]
            for t in times:
                angle=math.sin(t/duration*math.tau+phase)*amplitude*mult
                if joint in (11,14): angle=-abs(angle)
                quats.extend((math.sin(angle/2),0,0,math.cos(angle/2)))
            channel(joint,'rotation',quats,'VEC4')
        translations=[]
        for t in times: translations.extend((0,1+abs(math.sin(t/duration*math.tau))*.035,0))
        channel(0,'translation',translations,'VEC3')
        doc['animations']=[{'name':name,'samplers':samplers,'channels':channels}]
    doc.update({'bufferViews':views,'accessors':accessors,'buffers':[{'byteLength':len(blob)}]})
    data=json.dumps(doc,separators=(',',':')).encode()
    data+=b' '*((-len(data))%4);blob.extend(b'\0'*((-len(blob))%4))
    total=12+8+len(data)+8+len(blob)
    (DEST/(name+'.glb')).write_bytes(struct.pack('<III',0x46546C67,2,total)+struct.pack('<II',len(data),0x4E4F534A)+data+struct.pack('<II',len(blob),0x004E4942)+blob)
    print(name, total, 'bytes')

make('StudioBot_Idle',3.2,.07,[.64,.72,.52])
make('StudioBot_Walk',1.2,.6,[.64,.72,.52])
make('StudioBot_Run',.7,1.0,[.72,.52,.34])
make('StudioBot_Rig',1,0,[.47,.59,.69],False)
