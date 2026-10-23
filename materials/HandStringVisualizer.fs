/*{
    "DESCRIPTION": "A colorful 3D fist pulling a glowing string. The string's harmonics are driven by the music's spectrum, the waveform ripples along it, and bass hits jerk the hand upward.",
    "CREDIT": "madmapper-agent",
    "ISFVSN": "2",
    "CATEGORIES": ["Generator", "Audio Reactive"],
    "INPUTS": [
        { "NAME": "waveform", "TYPE": "audio", "MAX": 256 },
        { "NAME": "spectrum", "TYPE": "audioFFT", "MAX": 64 },
        { "NAME": "sensitivity", "LABEL": "Sensitivity", "TYPE": "float", "DEFAULT": 1.5, "MIN": 0.0, "MAX": 4.0 },
        { "NAME": "colorMode", "LABEL": "Palette", "TYPE": "long", "DEFAULT": 0, "VALUES": [0, 1, 2, 3], "LABELS": ["Rainbow", "Neon", "Sunset", "Ice"] },
        { "NAME": "hueShift", "LABEL": "Hue Shift", "TYPE": "float", "DEFAULT": 0.0, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "hueSpeed", "LABEL": "Hue Speed", "TYPE": "float", "DEFAULT": 0.05, "MIN": 0.0, "MAX": 0.5 },
        { "NAME": "saturation", "LABEL": "Saturation", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 1.5 },
        { "NAME": "pullAmount", "LABEL": "Pull Amount", "TYPE": "float", "DEFAULT": 0.5, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "pullSpeed", "LABEL": "Pull Speed", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 4.0 },
        { "NAME": "bassKick", "LABEL": "Bass Kick", "TYPE": "float", "DEFAULT": 0.6, "MIN": 0.0, "MAX": 2.0 },
        { "NAME": "stringWidth", "LABEL": "String Width", "TYPE": "float", "DEFAULT": 0.05, "MIN": 0.015, "MAX": 0.12 },
        { "NAME": "glow", "LABEL": "Glow", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 3.0 },
        { "NAME": "sway", "LABEL": "Sway", "TYPE": "float", "DEFAULT": 0.5, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "background", "LABEL": "Background", "TYPE": "float", "DEFAULT": 0.6, "MIN": 0.0, "MAX": 1.0 }
    ]
}*/

// Written as GLSL ES 1.0-compatible ISF (constant loop bounds, no arrays) so
// it runs in MadMapper and in the WebGL preview.

#define PI 3.14159265
#define MODES 8
#define ANCHOR_Y -1.9

// ---------------------------------------------------------------- audio

float spec(float x) {
    return IMG_NORM_PIXEL(spectrum, vec2(clamp(x, 0.0, 1.0), 0.5)).r;
}

float wave(float x) {
    // light smoothing so the string ripples instead of zig-zagging
    float d = 1.0 / 128.0;
    float s = IMG_NORM_PIXEL(waveform, vec2(clamp(x - d, 0.0, 1.0), 0.5)).r
            + IMG_NORM_PIXEL(waveform, vec2(clamp(x, 0.0, 1.0), 0.5)).r * 2.0
            + IMG_NORM_PIXEL(waveform, vec2(clamp(x + d, 0.0, 1.0), 0.5)).r;
    return s * 0.25 - 0.5;
}

float bassLevel() {
    return (spec(0.01) + spec(0.03) + spec(0.05) + spec(0.08)) * 0.25;
}

// ---------------------------------------------------------------- color

vec3 palette(float t) {
    float m = float(colorMode);
    vec3 a = vec3(0.5), b = vec3(0.5), c = vec3(1.0), d = vec3(0.0, 0.33, 0.67);  // rainbow
    if (m > 0.5 && m < 1.5) { a = vec3(0.55, 0.35, 0.65); b = vec3(0.45, 0.45, 0.35); d = vec3(0.75, 0.05, 0.35); }  // neon
    if (m > 1.5 && m < 2.5) { a = vec3(0.65, 0.35, 0.35); b = vec3(0.35, 0.3, 0.3); c = vec3(1.0, 1.0, 0.5); d = vec3(0.0, 0.12, 0.25); }  // sunset
    if (m > 2.5) { a = vec3(0.5, 0.65, 0.8); b = vec3(0.3, 0.3, 0.2); d = vec3(0.55, 0.6, 0.65); }  // ice
    vec3 col = a + b * cos(2.0 * PI * (c * (t + hueShift) + d));
    float g = dot(col, vec3(0.299, 0.587, 0.114));
    return max(mix(vec3(g), col, saturation), 0.0);
}

// ---------------------------------------------------------------- shapes

float smin(float a, float b, float k) {
    float h = clamp(0.5 + 0.5 * (b - a) / k, 0.0, 1.0);
    return mix(b, a, h) - k * h * (1.0 - h);
}

float sdCapsule(vec3 p, vec3 a, vec3 b, float r) {
    vec3 pa = p - a, ba = b - a;
    float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
    return length(pa - ba * h) - r;
}

float sdRoundBox(vec3 p, vec3 b, float r) {
    vec3 q = abs(p) - b;
    return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0) - r;
}

// One finger wrapped around the string (string runs along local y at x=z=0).
float sdFinger(vec3 q, float y, float s) {
    vec3 k  = vec3(-0.33, y, 0.10);
    vec3 p1 = vec3(-0.10, y, 0.215) * vec3(1.0, 1.0, s);
    vec3 p2 = vec3( 0.15 * s, y, 0.14 * s);
    vec3 p3 = vec3( 0.19 * s, y - 0.01, -0.06);
    float r = 0.085 * mix(0.8, 1.0, s);
    float d = sdCapsule(q, k, p1, r);
    d = smin(d, sdCapsule(q, p1, p2, r * 0.95), 0.03);
    d = smin(d, sdCapsule(q, p2, p3, r * 0.88), 0.03);
    return d;
}

float sdHand(vec3 q) {
    // back of the hand / palm block, left of the string
    float d = sdRoundBox(q - vec3(-0.45, 0.0, 0.0), vec3(0.13, 0.30, 0.11), 0.09);
    // fingers, index (top) to pinky (bottom)
    d = smin(d, sdFinger(q,  0.255, 1.0), 0.05);
    d = smin(d, sdFinger(q,  0.085, 1.0), 0.05);
    d = smin(d, sdFinger(q, -0.085, 0.97), 0.05);
    d = smin(d, sdFinger(q, -0.245, 0.88), 0.05);
    // thumb folded over the index and middle fingers
    float t = sdCapsule(q, vec3(-0.40, 0.20, 0.12), vec3(-0.20, 0.35, 0.24), 0.10);
    t = smin(t, sdCapsule(q, vec3(-0.20, 0.35, 0.24), vec3(0.01, 0.30, 0.29), 0.083), 0.04);
    d = smin(d, t, 0.06);
    // wrist and forearm reaching in from the upper left
    float arm = sdCapsule(q, vec3(-0.58, 0.12, -0.02), vec3(-1.9, 2.1, -0.6), 0.22);
    d = smin(d, arm, 0.14);
    return d;
}

// ---------------------------------------------------------------- scene

// Shared per-frame values, set once in main().
vec3 gHandPos;
float gSwayAng;
float gFistBottom;
float gModeAmp1, gModeAmp2, gModeAmp3, gModeAmp4, gModeAmp5, gModeAmp6, gModeAmp7, gModeAmp8;

float modeAmp(int n) {
    if (n == 1) return gModeAmp1;
    if (n == 2) return gModeAmp2;
    if (n == 3) return gModeAmp3;
    if (n == 4) return gModeAmp4;
    if (n == 5) return gModeAmp5;
    if (n == 6) return gModeAmp6;
    if (n == 7) return gModeAmp7;
    return gModeAmp8;
}

// Sideways displacement of the string at height y: a sum of standing-wave
// harmonics (one per spectrum band) plus the raw waveform.
vec2 stringOffset(float y) {
    float len = gFistBottom - ANCHOR_Y;
    float t = clamp((y - ANCHOR_Y) / len, 0.0, 1.0);
    vec2 off = vec2(0.0);
    for (int i = 1; i <= MODES; i++) {
        float n = float(i);
        float shape = sin(n * PI * t);
        float ph = TIME * (5.0 + 3.0 * n);
        off += modeAmp(i) * shape * vec2(sin(ph), 0.35 * cos(ph * 1.3));
    }
    off.x += wave(t) * 0.22 * sensitivity * sin(PI * t);
    return off;
}

vec3 toLocal(vec3 p) {
    vec3 q = p - gHandPos;
    float c = cos(gSwayAng), s = sin(gSwayAng);
    return vec3(c * q.x + s * q.z, q.y, -s * q.x + c * q.z);
}

float sdString(vec3 p) {
    vec3 q = toLocal(p);
    vec2 off = q.y < -0.3 ? stringOffset(p.y) : vec2(0.0);
    float top = 0.75;
    float body = length(q.xz - off) - stringWidth;
    float cap = max(body, q.y - top);
    // keep it a bounded tube ending at the anchor
    return max(cap, ANCHOR_Y - 0.5 - p.y) * 0.6;
}

vec2 map(vec3 p) {
    float h = sdHand(toLocal(p));
    float s = sdString(p);
    return h < s ? vec2(h, 1.0) : vec2(s, 2.0);
}

vec3 calcNormal(vec3 p) {
    vec2 e = vec2(0.0015, 0.0);
    return normalize(vec3(
        map(p + e.xyy).x - map(p - e.xyy).x,
        map(p + e.yxy).x - map(p - e.yxy).x,
        map(p + e.yyx).x - map(p - e.yyx).x));
}

float calcAO(vec3 p, vec3 n) {
    float occ = 0.0, w = 1.0;
    for (int i = 1; i <= 4; i++) {
        float h = 0.03 * float(i);
        occ += (h - map(p + n * h).x) * w;
        w *= 0.7;
    }
    return clamp(1.0 - 4.0 * occ, 0.0, 1.0);
}

vec3 stringColor(float y, float hue) {
    float t = clamp((y - ANCHOR_Y) / (gFistBottom - ANCHOR_Y), 0.0, 1.0);
    float band = spec(0.02 + 0.5 * t * t);
    float pulse = 0.5 + 0.5 * sin(t * 18.0 - TIME * 6.0);
    vec3 c = palette(hue + 0.6 * t);
    return c * (1.0 + 1.2 * band * sensitivity + 0.5 * pulse);
}

void main() {
    vec2 res = RENDERSIZE;
    vec2 uv = (gl_FragCoord.xy - 0.5 * res) / res.y;

    float bass = min(bassLevel() * sensitivity, 1.5);
    float hue = TIME * hueSpeed;

    // harmonics: low modes follow the bass, high modes the treble
    gModeAmp1 = 0.010 + 0.16 * spec(0.02) * sensitivity;
    gModeAmp2 = 0.008 + 0.11 * spec(0.05) * sensitivity;
    gModeAmp3 = 0.08 * spec(0.09) * sensitivity;
    gModeAmp4 = 0.06 * spec(0.15) * sensitivity;
    gModeAmp5 = 0.045 * spec(0.23) * sensitivity;
    gModeAmp6 = 0.035 * spec(0.33) * sensitivity;
    gModeAmp7 = 0.028 * spec(0.46) * sensitivity;
    gModeAmp8 = 0.022 * spec(0.62) * sensitivity;

    float cycle = 0.5 - 0.5 * cos(TIME * pullSpeed);
    gHandPos = vec3(0.15, 0.3 * pullAmount * cycle + 0.14 * min(bassKick * bass, 1.6), 0.0);
    gSwayAng = sway * (0.35 * sin(TIME * 0.4) - 0.25);
    gFistBottom = gHandPos.y - 0.33;

    vec3 ro = vec3(0.0, 0.3, 4.0);
    vec3 ta = vec3(0.0, 0.1, 0.0);
    vec3 ww = normalize(ta - ro);
    vec3 uu = normalize(cross(ww, vec3(0.0, 1.0, 0.0)));
    vec3 vv = cross(uu, ww);
    vec3 rd = normalize(uv.x * uu + uv.y * vv + 1.7 * ww);

    // background: dark gradient, spectrum aurora rising from the bottom,
    // and rings rippling out from the hand on bass
    vec3 col = palette(hue + 0.15 * uv.y + 0.5) * (0.05 + 0.06 * (0.5 - uv.y)) * background;
    float bar = spec(0.03 + 0.6 * abs(uv.x));
    col += palette(hue + abs(uv.x)) * bar * exp(-3.0 * (uv.y + 0.5)) * 0.35 * background * sensitivity;
    vec2 handScreen = vec2(0.03, 0.42 * (gHandPos.y - 0.1));
    float r = length(uv - handScreen);
    float rings = smoothstep(0.92, 1.0, sin(r * 22.0 - TIME * 3.0 - bass * 6.0)) * exp(-2.5 * r);
    col += palette(hue + r) * rings * 0.25 * background * (0.3 + bass);

    // raymarch
    float t = 0.0;
    float m = 0.0;
    float minString = 1e3;
    for (int i = 0; i < 110; i++) {
        vec3 p = ro + rd * t;
        vec2 h = map(p);
        minString = min(minString, sdString(p) / 0.6);
        if (h.x < 0.001) { m = h.y; break; }
        t += h.x;
        if (t > 9.0) break;
    }

    if (m > 0.5) {
        vec3 p = ro + rd * t;
        vec3 n = calcNormal(p);
        vec3 sc = stringColor(clamp(p.y, ANCHOR_Y, gFistBottom), hue);
        if (m < 1.5) {
            vec3 q = toLocal(p);
            float fres = pow(1.0 - max(dot(n, -rd), 0.0), 3.0);
            vec3 albedo = palette(hue + 0.45 * q.y + 0.3 * q.x + 0.35 * n.x - 0.25 * n.y + 0.4 * fres);
            vec3 key = normalize(vec3(0.5, 0.8, 0.6));
            float dif = max(dot(n, key), 0.0);
            float spe = pow(max(dot(reflect(rd, n), key), 0.0), 24.0);
            float ao = calcAO(p, n);
            vec3 lit = albedo * (0.25 + 0.95 * dif) * ao;
            lit += palette(hue + 0.5) * fres * 1.4;
            lit += vec3(spe) * 0.6;
            // light thrown onto the fist by the glowing string
            float ds = max(sdString(p) / 0.6, 0.0);
            lit += sc * 0.5 * glow / (1.0 + 60.0 * ds * ds);
            col = lit;
        } else {
            float fres = pow(1.0 - max(dot(n, -rd), 0.0), 2.0);
            col = sc * (0.8 + 0.6 * fres);
        }
    }

    // halo around the string
    float yGuess = ro.y + rd.y * 4.0;
    vec3 halo = stringColor(clamp(yGuess, ANCHOR_Y, gFistBottom), hue);
    col += halo * glow * 0.035 / (0.004 + minString * minString) * 0.02;
    col += halo * glow * exp(-minString * 12.0) * 0.12 * (0.6 + 0.5 * bass);

    // tone map + vignette
    col = 1.0 - exp(-col * 1.1);
    col *= 1.0 - 0.35 * dot(uv, uv);
    gl_FragColor = vec4(pow(col, vec3(0.92)), 1.0);
}
