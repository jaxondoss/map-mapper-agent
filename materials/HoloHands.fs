/*{
    "DESCRIPTION": "Two holographic hands made of glowing dots reach in from the sides and pinch toward the center, then dissolve into particles. Drive 'reach', 'pinch' and 'dissolve' (0 to 1) to play the gesture.",
    "CREDIT": "madmapper-agent",
    "ISFVSN": "2",
    "CATEGORIES": ["Generator", "Projection Mapping"],
    "INPUTS": [
        { "NAME": "reach", "LABEL": "Reach", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "pinch", "LABEL": "Pinch", "TYPE": "float", "DEFAULT": 0.6, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "dissolve", "LABEL": "Dissolve", "TYPE": "float", "DEFAULT": 0.0, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "target", "LABEL": "Pinch Target", "TYPE": "point2D", "DEFAULT": [0.5, 0.55], "MIN": [0.0, 0.0], "MAX": [1.0, 1.0] },
        { "NAME": "handSize", "LABEL": "Hand Size", "TYPE": "float", "DEFAULT": 0.7, "MIN": 0.2, "MAX": 1.2 },
        { "NAME": "dotSize", "LABEL": "Dot Spacing", "TYPE": "float", "DEFAULT": 0.018, "MIN": 0.006, "MAX": 0.06 },
        { "NAME": "color", "LABEL": "Color", "TYPE": "color", "DEFAULT": [0.3, 0.75, 1.0, 1.0] },
        { "NAME": "glow", "LABEL": "Glow", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 3.0 },
        { "NAME": "flicker", "LABEL": "Flicker", "TYPE": "float", "DEFAULT": 0.3, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "intensity", "LABEL": "Intensity", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 2.0 },
        { "NAME": "loop", "LABEL": "Loop (ignore controls)", "TYPE": "bool", "DEFAULT": false }
    ]
}*/

#define FOCAL 1.8
#define ROLL 0.75   // fingers lean toward the center
#define YAW 0.55    // palm turns toward the other hand
#define GAP 0.07   // space left between the two index fingertips
#define CAM_Z 4.0

float hash(float n) { return fract(sin(n) * 43758.5453); }
float hash2(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

float smin(float a, float b, float k) {
    float h = clamp(0.5 + 0.5 * (b - a) / k, 0.0, 1.0);
    return mix(b, a, h) - k * h * (1.0 - h);
}

vec2 rot2(vec2 v, float a) {
    return vec2(cos(a) * v.x - sin(a) * v.y, sin(a) * v.x + cos(a) * v.y);
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

// ------------------------------------------------------------ hand pose
// Hand space: wrist at the origin, fingers along +y, palm facing +z,
// thumb on the -x side. Joint positions are computed once per frame.

vec3 gI0, gI1, gI2, gI3;   // index
vec3 gM0, gM1, gM2, gM3;   // middle
vec3 gR0, gR1, gR2, gR3;   // ring
vec3 gP0, gP1, gP2, gP3;   // pinky
vec3 gT0, gT1, gT2;        // thumb
vec3 gWrist;               // right hand's wrist, relative to the mirror line
float gCX;                 // x of the mirror line (the pinch target)
float gScale;

vec3 bendDir(float sx, float a) { return normalize(vec3(sx, cos(a), sin(a))); }

void finger(vec3 k, float sx, vec3 len, vec3 curl,
            out vec3 p0, out vec3 p1, out vec3 p2, out vec3 p3) {
    p0 = k;
    p1 = p0 + len.x * bendDir(sx, curl.x);
    p2 = p1 + len.y * bendDir(sx, curl.x + curl.y);
    p3 = p2 + len.z * bendDir(sx, curl.x + curl.y + curl.z);
}

void setupPose(float pc) {
    finger(vec3(-0.22, 0.80, 0.0), -0.08, vec3(0.40, 0.24, 0.19),
           mix(vec3(0.20, 0.30, 0.20), vec3(0.55, 0.75, 0.45), pc), gI0, gI1, gI2, gI3);
    finger(vec3(-0.07, 0.83, 0.0), -0.02, vec3(0.44, 0.27, 0.20),
           mix(vec3(0.30, 0.45, 0.30), vec3(0.25, 0.35, 0.25), pc), gM0, gM1, gM2, gM3);
    finger(vec3(0.08, 0.81, 0.0), 0.05, vec3(0.41, 0.25, 0.19),
           mix(vec3(0.40, 0.60, 0.40), vec3(0.30, 0.45, 0.30), pc), gR0, gR1, gR2, gR3);
    finger(vec3(0.22, 0.75, 0.0), 0.14, vec3(0.32, 0.20, 0.17),
           mix(vec3(0.50, 0.70, 0.40), vec3(0.35, 0.50, 0.30), pc), gP0, gP1, gP2, gP3);

    gT0 = vec3(-0.27, 0.28, 0.07);
    vec3 openJ = gT0 + vec3(-0.26, 0.26, 0.16);
    vec3 openT = openJ + vec3(-0.06, 0.32, 0.12);
    vec3 pinchT = gI3 + vec3(0.0, -0.04, 0.07);
    vec3 pinchJ = mix(gT0, pinchT, 0.5) + vec3(-0.16, -0.04, 0.14);
    gT1 = mix(openJ, pinchJ, pc);
    gT2 = mix(openT, pinchT, pc);
}

float sdFinger(vec3 q, vec3 a, vec3 b, vec3 c, vec3 d, float r) {
    float f = sdCapsule(q, a, b, r);
    f = smin(f, sdCapsule(q, b, c, r * 0.92), 0.02);
    return smin(f, sdCapsule(q, c, d, r * 0.84), 0.02);
}

float sdHandLocal(vec3 q) {
    float d = sdRoundBox(q - vec3(0.0, 0.43, 0.0), vec3(0.24, 0.32, 0.055), 0.06);
    d = smin(d, sdCapsule(q, gT0, vec3(-0.05, 0.55, 0.05), 0.09), 0.08);   // thumb muscle
    d = smin(d, sdFinger(q, gI0, gI1, gI2, gI3, 0.066), 0.05);
    d = smin(d, sdFinger(q, gM0, gM1, gM2, gM3, 0.068), 0.05);
    d = smin(d, sdFinger(q, gR0, gR1, gR2, gR3, 0.064), 0.05);
    d = smin(d, sdFinger(q, gP0, gP1, gP2, gP3, 0.056), 0.05);
    float th = sdCapsule(q, gT0, gT1, 0.078);
    th = smin(th, sdCapsule(q, gT1, gT2, 0.066), 0.03);
    d = smin(d, th, 0.06);
    d = smin(d, sdCapsule(q, vec3(0.0, 0.08, -0.01), vec3(0.0, -3.0, -0.05), 0.17), 0.1);  // forearm
    return d;
}

// World -> right-hand space. Hands mirror across x = 0, so the left hand is
// the same shape reflected.
vec3 toHand(vec3 p) {
    vec3 q = vec3(abs(p.x - gCX), p.y, p.z) - gWrist;
    q.xy = rot2(q.xy, -ROLL);
    q.xz = rot2(q.xz, YAW);
    return q / gScale;
}

// Hand space -> world-space offset (inverse of toHand's rotation and scale).
vec3 handToWorld(vec3 l) {
    vec3 q = l * gScale;
    q.xz = rot2(q.xz, -YAW);
    q.xy = rot2(q.xy, ROLL);
    return q;
}

float map(vec3 p) {
    return sdHandLocal(toHand(p)) * gScale;
}

vec3 calcNormal(vec3 p) {
    vec2 e = vec2(0.002, 0.0);
    return normalize(vec3(
        map(p + e.xyy) - map(p - e.xyy),
        map(p + e.yxy) - map(p - e.yxy),
        map(p + e.yyx) - map(p - e.yyx)));
}

void main() {
    vec2 res = RENDERSIZE;
    vec2 uv = (gl_FragCoord.xy - 0.5 * res) / res.y;

    float r_ = reach, pc = pinch, ds = dissolve;
    if (loop) {
        float t = mod(TIME, 9.0);
        r_ = smoothstep(0.0, 2.5, t);
        pc = smoothstep(2.5, 4.5, t) * 0.9;
        ds = smoothstep(6.5, 8.5, t);
    }
    float er = 1.0 - pow(1.0 - clamp(r_, 0.0, 1.0), 3.0);   // ease out

    gScale = handSize;
    // Where the right hand's pinch should land (world space at z = 0).
    vec2 tgt = (target - 0.5) * vec2(res.x / res.y, 1.0) * (CAM_Z / FOCAL);
    gCX = tgt.x;
    // Place the wrist so the open hand's index fingertip ends just short of
    // the target; the hand then stays put while it pinches.
    setupPose(0.0);
    vec3 home = vec3(GAP, tgt.y, 0.0) - handToWorld(gI3);
    setupPose(clamp(pc, 0.0, 1.0));
    vec3 away = home + vec3(1.6, -1.4, 0.0);
    gWrist = mix(away, home, er) + vec3(0.0, 0.015 * sin(TIME * 1.3), 0.0);

    vec3 ro = vec3(0.0, 0.0, CAM_Z);
    vec3 rd = normalize(vec3(uv, -FOCAL));

    float t = 0.0, d = 1e3, minD = 1e3;
    bool hit = false;
    for (int i = 0; i < 100; i++) {
        vec3 p = ro + rd * t;
        d = map(p);
        minD = min(minD, d);
        if (d < 0.0015) { hit = true; break; }
        t += d * 0.9;
        if (t > 8.0) break;
    }

    vec3 col = vec3(0.0);
    float fadeIn = smoothstep(0.0, 0.25, r_);

    if (hit) {
        vec3 p = ro + rd * t;
        vec3 n = calcNormal(p);
        float fres = pow(1.0 - abs(dot(n, rd)), 2.0);

        // dots stuck to the hand: triplanar grid in hand-relative space
        vec3 hp = vec3(abs(p.x - gCX), p.y, p.z) - gWrist;
        vec3 an = abs(n);
        vec2 c = an.z > an.x && an.z > an.y ? hp.xy : (an.x > an.y ? hp.zy : hp.xz);
        vec2 cell = floor(c / dotSize);
        vec2 f = fract(c / dotSize) - 0.5;
        float px = t / (FOCAL * res.y) / dotSize;     // pixel size in cell units
        float id = hash2(cell + (p.x < gCX ? 17.0 : 0.0));
        float dotMask = 1.0 - smoothstep(0.26 - px, 0.26 + px, length(f));
        dotMask *= step(ds * 1.05, id) * (0.65 + 0.35 * id);

        float scan = 0.25 * smoothstep(0.85, 1.0, sin(p.y * 9.0 - TIME * 4.0));
        float fill = 0.10 * (1.0 - ds);
        float rim = fres * 0.9 * (1.0 - ds);
        col = color.rgb * (fill + dotMask * (0.55 + 0.6 * fres) + rim + scan * (1.0 - ds));
    }

    // halo around the silhouette
    col += color.rgb * exp(-minD * 40.0) * 0.25 * glow * (1.0 - 0.7 * ds);

    // dissolve: sparks drifting up out of the hand area
    if (ds > 0.0) {
        vec2 g = uv * 70.0 + vec2(0.0, -TIME * 6.0 - ds * 30.0);
        vec2 gc = floor(g);
        float h = hash2(gc);
        vec2 gp = fract(g) - 0.5 - (vec2(hash2(gc + 3.1), hash2(gc + 7.7)) - 0.5) * 0.6;
        float spark = (1.0 - smoothstep(0.08, 0.2, length(gp))) * step(0.82, h);
        float zone = exp(-minD * 9.0);
        col += color.rgb * spark * zone * sin(3.14159 * ds) * 1.5;
    }

    float fl = 1.0 - flicker * 0.35 * step(0.93, hash(floor(TIME * 18.0)));
    gl_FragColor = vec4(col * fadeIn * fl * intensity, 1.0);
}
