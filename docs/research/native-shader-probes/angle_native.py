"""Probe: run the GLSL fixture families through a native ANGLE (DLLs shipped with an Electron app), D3D11
backend, ES 3.0 context, fragment-shader readback through RGBA32UI targets. No browser.
usage: py -3 -I angle_native.py <angle-dll-dir> <fixture.json> <shaders-dir> <outdir> [webgl]"""
import ctypes as C, json, os, struct, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fam

dll, fx, sh, out = sys.argv[1:5]; webgl = len(sys.argv) > 5
os.add_dll_directory(dll)
egl = C.CDLL(os.path.join(dll, 'libEGL.dll')); gl = C.CDLL(os.path.join(dll, 'libGLESv2.dll'))
egl.eglGetProcAddress.restype = C.c_void_p
for n in ['eglInitialize', 'eglChooseConfig', 'eglCreatePbufferSurface', 'eglCreateContext', 'eglMakeCurrent', 'eglBindAPI', 'eglGetError']:
    getattr(egl, n).restype = C.c_void_p if n.startswith('eglCreate') else C.c_uint
egl.eglCreatePbufferSurface.argtypes = [C.c_void_p, C.c_void_p, C.POINTER(C.c_int)]
egl.eglCreateContext.argtypes = [C.c_void_p, C.c_void_p, C.c_void_p, C.POINTER(C.c_int)]
egl.eglMakeCurrent.argtypes = [C.c_void_p, C.c_void_p, C.c_void_p, C.c_void_p]
egl.eglInitialize.argtypes = [C.c_void_p, C.c_void_p, C.c_void_p]
egl.eglChooseConfig.argtypes = [C.c_void_p, C.POINTER(C.c_int), C.POINTER(C.c_void_p), C.c_int, C.POINTER(C.c_int)]
GPD = C.CFUNCTYPE(C.c_void_p, C.c_uint, C.c_void_p, C.POINTER(C.c_int))(egl.eglGetProcAddress(b'eglGetPlatformDisplayEXT'))
def ia(*v): return (C.c_int * (len(v) + 1))(*v, 0x3038)
disp = GPD(0x3202, None, ia(0x3203, 0x3208))  # EGL_PLATFORM_ANGLE_TYPE_ANGLE = D3D11
assert egl.eglInitialize(disp, None, None), 'eglInitialize %x' % egl.eglGetError()
cfg = C.c_void_p(); ncfg = C.c_int()
egl.eglChooseConfig(disp, ia(0x3033, 0x1, 0x3040, 0x40), C.byref(cfg), 1, C.byref(ncfg))  # PBUFFER, ES3
surf = egl.eglCreatePbufferSurface(disp, cfg, ia(0x3057, 16, 0x3056, 16))
egl.eglBindAPI(0x30A0)
ctx_attr = (0x3098, 3) + ((0x33AC, 1) if webgl else ())  # CLIENT_VERSION 3 [, WEBGL_COMPATIBILITY_ANGLE]
ctx = egl.eglCreateContext(disp, cfg, None, ia(*ctx_attr))
assert ctx and egl.eglMakeCurrent(disp, surf, surf, ctx), 'context %x' % egl.eglGetError()
gl.glGetString.restype = C.c_char_p
print('GL_RENDERER', gl.glGetString(0x1F01).decode()); print('GL_VERSION', gl.glGetString(0x1F02).decode())
print('webgl compatibility context', webgl)

gl.glCreateShader.restype = C.c_uint; gl.glCreateProgram.restype = C.c_uint
def shader(kind, src):
    s = gl.glCreateShader(kind); b = C.c_char_p(src.encode()); gl.glShaderSource(s, 1, C.byref(b), None); gl.glCompileShader(s)
    ok = C.c_int(); gl.glGetShaderiv(s, 0x8B81, C.byref(ok))
    if not ok.value:
        buf = C.create_string_buffer(4000); gl.glGetShaderInfoLog(s, 4000, None, buf); raise RuntimeError(buf.value.decode()[:300])
    return s
VS = '#version 300 es\nvoid main() { vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); }\n'
lib = open(os.environ['LIBGLSL']).read() if os.environ.get('LIBGLSL') else ''.join(open(os.path.join(sh, f)).read() for f in ['CultMath.glsl', 'CultMath.Cycles.glsl', 'CultMath.Phacelle.glsl'])
vao = C.c_uint(); gl.glGenVertexArrays(1, C.byref(vao)); gl.glBindVertexArray(vao)
d = json.load(open(fx)); man = []; skipped = 0
for idx, f in enumerate(d['functions']):
    r, name, ps = fam.parse(f['glsl']); rt = fam.ty(r); pts = [fam.ty(p) for p in ps]
    if rt is None or any(p is None for p in pts): skipped += 1; continue
    nin = sum(p[1] for p in pts); nout = rt[1]; tw = (nin + 3) // 4; nt = (nout + 3) // 4; tag = 'f%03d' % idx
    words = [int(x, 16) for c in f['cases'] for x in c.split('->')[0].split()]
    tex = []
    for ci in range(256):
        row = words[ci * nin:(ci + 1) * nin]; tex += row + [0] * (tw * 4 - nin)
    lines, off = [], 0
    for j, (p, (ht, n, k)) in enumerate(zip(ps, pts)):
        src = ', '.join('w[%d]' % (off + t) for t in range(n)); v = ('uvec%d(%s)' % (n, src)) if n > 1 else src
        conv = {'f': 'uintBitsToFloat', 'i': ('ivec%d' % n if n > 1 else 'int'), 'u': ''}[k]
        lines.append('    %s a%d = %s(%s);' % (p, j, conv, v)); off += n
    lines.append('    %s r = %s(%s);' % (r, name, ', '.join('a%d' % j for j in range(len(ps)))))
    comps = ['r[%d][%d]' % (a, b) for a in range(3) for b in range(3)] if r == 'mat3' else ['r'] if nout == 1 else ['r[%d]' % i for i in range(nout)]
    for i, c in enumerate(comps):
        lines.append('    ow[%d] = %s(%s);' % (i, {'f': 'floatBitsToUint', 'i': 'uint', 'u': ''}[rt[2]], c))
    outs = ''.join('layout(location = %d) out highp uvec4 o%d;\n' % (t, t) for t in range(nt))
    wr = ''.join('    o%d = uvec4(ow[%d], ow[%d], ow[%d], ow[%d]);\n' % (t, 4 * t, 4 * t + 1, 4 * t + 2, 4 * t + 3) for t in range(nt))
    FS = ('#version 300 es\nprecision highp float;\nprecision highp int;\n' + lib + '\nuniform highp usampler2D In;\n' + outs +
          'void main() {\n    int i = int(gl_FragCoord.y) * 16 + int(gl_FragCoord.x);\n    uint w[%d];\n    for (int k = 0; k < %d; k++) w[k] = texelFetch(In, ivec2(k / 4, i), 0)[k %% 4];\n'
          '    uint ow[%d];\n' % (tw * 4, nin, nt * 4) + '\n'.join(lines) + '\n' + ''.join('    ow[%d] = 0u;\n' % z for z in range(nout, nt * 4)) + wr + '}\n')
    try:
        prog = gl.glCreateProgram(); gl.glAttachShader(prog, shader(0x8B31, VS)); gl.glAttachShader(prog, shader(0x8B30, FS)); gl.glLinkProgram(prog)
    except RuntimeError as e:
        print('compile fail', f['glsl'], str(e).replace('\n', ' ')[:200]); continue
    gl.glUseProgram(prog)
    t_in = C.c_uint(); gl.glGenTextures(1, C.byref(t_in)); gl.glActiveTexture(0x84C0); gl.glBindTexture(0x0DE1, t_in)
    gl.glTexParameteri(0x0DE1, 0x2801, 0x2600); gl.glTexParameteri(0x0DE1, 0x2800, 0x2600)
    buf = (C.c_uint * len(tex))(*tex); gl.glTexImage2D(0x0DE1, 0, 0x8D70, tw, 256, 0, 0x8D99, 0x1405, buf)
    gl.glUniform1i(gl.glGetUniformLocation(prog, b'In'), 0)
    fbo = C.c_uint(); gl.glGenFramebuffers(1, C.byref(fbo)); gl.glBindFramebuffer(0x8D40, fbo)
    tos = (C.c_uint * nt)(); gl.glGenTextures(nt, tos)
    for t in range(nt):
        gl.glBindTexture(0x0DE1, tos[t]); gl.glTexImage2D(0x0DE1, 0, 0x8D70, 16, 16, 0, 0x8D99, 0x1405, None)
        gl.glFramebufferTexture2D(0x8D40, 0x8CE0 + t, 0x0DE1, tos[t], 0)
    gl.glDrawBuffers(nt, (C.c_uint * nt)(*[0x8CE0 + t for t in range(nt)]))
    gl.glActiveTexture(0x84C0); gl.glBindTexture(0x0DE1, t_in)
    gl.glViewport(0, 0, 16, 16); gl.glDrawArrays(0x0004, 0, 3)
    res = [[0] * 1024 for _ in range(nt)]
    for t in range(nt):
        gl.glReadBuffer(0x8CE0 + t); px = (C.c_uint * 1024)(); gl.glReadPixels(0, 0, 16, 16, 0x8D99, 0x1405, px); res[t] = list(px)
    flat = []
    for ci in range(256):
        allw = sum((res[t][ci * 4:ci * 4 + 4] for t in range(nt)), []); flat += allw[:nout]
    open(os.path.join(out, tag + '.in'), 'wb').write(b'\0')
    open(os.path.join(out, tag + '.angle.out'), 'wb').write(struct.pack('<%dI' % len(flat), *flat))
    man.append({'tag': tag, 'glsl': f['glsl'], 'n': 256, 'nout': nout, 'kind': rt[2], 'tol': f['tolerance'], 'platform': f.get('platform')})
    gl.glDeleteFramebuffers(1, C.byref(fbo)); gl.glDeleteTextures(nt, tos); gl.glDeleteTextures(1, C.byref(t_in)); gl.glDeleteProgram(prog)
print('gl error at end 0x%x' % gl.glGetError(), '; ran', len(man), 'skipped struct', skipped)
json.dump(man, open(os.path.join(out, 'manifest.json'), 'w'))
fam.cmp(fx, out, 'angle')
