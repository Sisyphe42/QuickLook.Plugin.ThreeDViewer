# Test Models

3D 预览插件的测试样本。小样本全部入库；三个大样本（big.ply / huge.ply /
heavy.stl）只保留本地、不入 git（.gitignore 排除）。

## 重新生成大样本

```bash
# huge.ply（60 万面 ASCII PLY）与 big.ply（33 万面）
python - <<'PY'
def ply_grid(path, N):
    with open(path,'w') as f:
        f.write("ply\nformat ascii 1.0\nelement vertex %d\nproperty float x\nproperty float y\nproperty float z\nelement face %d\nproperty list uchar int vertex_indices\nend_header\n"%(N*N,(N-1)*(N-1)*2))
        for j in range(N):
            for i in range(N): f.write("%.4f %.4f %.4f\n"%(i*0.01,j*0.01,0.0))
        for j in range(N-1):
            for i in range(N-1):
                a=j*N+i;b=a+1;c=a+N;d=c+1
                f.write("3 %d %d %d\n"%(a,b,d)); f.write("3 %d %d %d\n"%(a,d,c))
ply_grid("big.ply", 410)   # 334,562 faces
ply_grid("huge.ply", 550)  # 602,802 faces
PY

# heavy.stl（62.5 万面二进制 STL）
python - <<'PY'
import struct
n=560
tris=[]
for j in range(n-1):
    for i in range(n-1):
        a=(i*0.01,j*0.01,0.0); b=((i+1)*0.01,j*0.01,0.0); c=((i+1)*0.01,(j+1)*0.01,0.0); d=(i*0.01,(j+1)*0.01,0.0)
        tris.append((a,b,c)); tris.append((a,c,d))
with open("heavy.stl","wb") as f:
    f.write(b"\0"*80); f.write(struct.pack("<I",len(tris)))
    for t in tris:
        f.write(struct.pack("<3f",0,0,1))
        for v in t: f.write(struct.pack("<3f",*v))
        f.write(struct.pack("<H",0))
PY
```

## 样本清单

| 样本 | 验证点 |
|---|---|
| `sample.*`（11 种格式） | 各格式导入通路 |
| `rich.glb` | PBR 因子、4 贴图槽、双动画、内嵌贴图 |
| `bones.glb` | 骨骼蒙皮（12 bones）+ 骨骼动画 |
| `multi-anim.glb` / `hierarchy.glb` | 动画通道 / 多级节点变换 |
| `glass-glow.glb` | alpha 混合与自发光 |
| `gltf-with-textures\`、`obj-with-mtl\` | 外部依赖解析 |
| `knot.stl`、`bumpy.stl`、`twomat.obj`、`scene.3ds` | 平滑法线 / 多材质 |
| `big.ply`（33 万面，本地） | 不触发降级 |
| `huge.ply`、`heavy.stl`（>50 万面，本地） | 降级抽样 + 提示条 |
