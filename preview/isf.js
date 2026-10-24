// Minimal ISF host shared by the preview pages: enough of the ISF 2 spec for
// generator shaders with float/long/bool/color/point2D inputs and
// audio/audioFFT textures.

export const WAVE_N = 256, FFT_N = 64;
const GLSL_TYPE = { float: "float", long: "int", bool: "bool", color: "vec4", point2D: "vec2", event: "bool" };

function compile(gl, type, src) {
  const s = gl.createShader(type);
  gl.shaderSource(s, src); gl.compileShader(s);
  if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
  return s;
}

function makeTex(gl) {
  const t = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, t);
  for (const [k, v] of [[gl.TEXTURE_MIN_FILTER, gl.LINEAR], [gl.TEXTURE_MAG_FILTER, gl.LINEAR],
                        [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) gl.texParameteri(gl.TEXTURE_2D, k, v);
  return t;
}

export class IsfLayer {
  constructor(gl, src) {
    this.gl = gl;
    const m = src.match(/\/\*([\s\S]*?)\*\//);
    const meta = JSON.parse(m[1]);
    this.inputs = meta.INPUTS || [];
    this.values = {};
    let decl = "precision highp float;\nprecision highp int;\nuniform float TIME;\nuniform vec2 RENDERSIZE;\nvarying vec2 isf_FragNormCoord;\n#define IMG_NORM_PIXEL(img, c) texture2D(img, c)\n";
    for (const i of this.inputs) {
      if (["audio", "audioFFT", "image"].includes(i.TYPE)) decl += `uniform sampler2D ${i.NAME};\n`;
      else decl += `uniform ${GLSL_TYPE[i.TYPE]} ${i.NAME};\n`;
      if (i.DEFAULT !== undefined) this.values[i.NAME] = i.DEFAULT;
    }
    const frag = decl + src.slice(m.index + m[0].length);
    const vert = "attribute vec2 pos; varying vec2 isf_FragNormCoord; void main(){ isf_FragNormCoord = pos*0.5+0.5; gl_Position = vec4(pos,0.,1.); }";
    const p = this.program = gl.createProgram();
    gl.attachShader(p, compile(gl, gl.VERTEX_SHADER, vert));
    gl.attachShader(p, compile(gl, gl.FRAGMENT_SHADER, frag));
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
    this.uniforms = {};
    for (const n of ["TIME", "RENDERSIZE", ...this.inputs.map(i => i.NAME)]) this.uniforms[n] = gl.getUniformLocation(p, n);
    this.buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, this.buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    this.posLoc = gl.getAttribLocation(p, "pos");
    this.waveTex = makeTex(gl); this.fftTex = makeTex(gl);
  }

  input(name) { return this.inputs.find(i => i.NAME.toLowerCase() === name.toLowerCase()); }

  // Draw into the viewport rect [x, y, w, h] (pixels, y from the bottom).
  render(time, rect, audio) {
    const gl = this.gl;
    gl.useProgram(this.program);
    gl.viewport(...rect);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.buf);
    gl.enableVertexAttribArray(this.posLoc);
    gl.vertexAttribPointer(this.posLoc, 2, gl.FLOAT, false, 0, 0);
    gl.uniform1f(this.uniforms.TIME, time);
    gl.uniform2f(this.uniforms.RENDERSIZE, rect[2], rect[3]);
    let unit = 0;
    for (const i of this.inputs) {
      const u = this.uniforms[i.NAME], v = this.values[i.NAME];
      if (i.TYPE === "audio" || i.TYPE === "audioFFT") {
        const tex = i.TYPE === "audio" ? this.waveTex : this.fftTex;
        const data = i.TYPE === "audio" ? audio.wave : audio.fft;
        gl.activeTexture(gl.TEXTURE0 + unit); gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.LUMINANCE, data.length, 1, 0, gl.LUMINANCE, gl.UNSIGNED_BYTE, data);
        gl.uniform1i(u, unit++);
      } else if (i.TYPE === "float") gl.uniform1f(u, v);
      else if (i.TYPE === "long") gl.uniform1i(u, v | 0);
      else if (i.TYPE === "bool" || i.TYPE === "event") gl.uniform1i(u, v ? 1 : 0);
      else if (i.TYPE === "color") gl.uniform4fv(u, v);
      else if (i.TYPE === "point2D") gl.uniform2fv(u, v);
    }
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
  }
}

// Audio source: a synthetic beat by default, or a live analyser (mic or file).
export class AudioFeed {
  constructor() {
    this.wave = new Uint8Array(WAVE_N); this.fft = new Uint8Array(FFT_N);
    this.analyser = null;
  }

  update(t) { if (this.analyser) this.live(); else this.demo(t); }

  // four-on-the-floor kick, offbeat hats, a wandering bassline
  demo(t) {
    const beat = t * 2.0, ph = beat % 1;
    const kick = Math.exp(-ph * 7), hat = Math.exp(-((beat + 0.5) % 1) * 18);
    const mid = 0.45 + 0.25 * Math.sin(t * 0.7);
    for (let i = 0; i < FFT_N; i++) {
      const x = i / FFT_N;
      let v = kick * Math.exp(-x * 14) + mid * Math.exp(-Math.pow((x - 0.18 - 0.05 * Math.sin(t)) * 9, 2)) * 0.8 + hat * Math.exp(-Math.pow((x - 0.7) * 5, 2)) * 0.6;
      v *= 0.85 + 0.15 * Math.sin(i * 1.7 + t * 9);
      this.fft[i] = Math.max(0, Math.min(255, v * 255));
    }
    for (let i = 0; i < WAVE_N; i++) {
      const s = i / WAVE_N;
      const v = kick * Math.sin(s * 2 * Math.PI * 3 + t * 40) * 0.35 + mid * Math.sin(s * 2 * Math.PI * 11 + t * 25) * 0.15 + hat * Math.sin(s * 997 + t * 300) * 0.1;
      this.wave[i] = 128 + Math.max(-127, Math.min(127, v * 127));
    }
  }

  live() {
    const a = this.analyser;
    const f = new Uint8Array(a.frequencyBinCount); a.getByteFrequencyData(f);
    // squash the analyser's linear bins onto 64 log-ish bands
    for (let i = 0; i < FFT_N; i++) {
      const lo = Math.floor(Math.pow(i / FFT_N, 2) * f.length * 0.6);
      const hi = Math.max(lo + 1, Math.floor(Math.pow((i + 1) / FFT_N, 2) * f.length * 0.6));
      let s = 0; for (let j = lo; j < hi; j++) s += f[j]; this.fft[i] = s / (hi - lo);
    }
    const w = new Uint8Array(a.fftSize); a.getByteTimeDomainData(w);
    for (let i = 0; i < WAVE_N; i++) this.wave[i] = w[Math.floor(i * w.length / WAVE_N)];
  }

  async useMic() {
    const ctx = new AudioContext(), stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    this.analyser = this.makeAnalyser(ctx);
    ctx.createMediaStreamSource(stream).connect(this.analyser);
  }

  useFile(file) {
    const ctx = new AudioContext(), audio = new Audio(URL.createObjectURL(file));
    this.analyser = this.makeAnalyser(ctx);
    ctx.createMediaElementSource(audio).connect(this.analyser);
    this.analyser.connect(ctx.destination);
    audio.play();
  }

  useDemo() { this.analyser = null; }

  makeAnalyser(ctx) {
    const a = ctx.createAnalyser(); a.fftSize = 2048; a.smoothingTimeConstant = 0.7; return a;
  }
}
