/*{
    "DESCRIPTION": "Icarus Falling: a holographic wireframe Icarus beats feathered wings toward a projected sun, loses his burning feathers and tumbles into a projected sea. A 20-second seamless loop, ray traced live in the shader (same scene as scenes/icarus_fall.html).",
    "CREDIT": "madmapper-agent",
    "ISFVSN": "2",
    "CATEGORIES": ["Generator", "Projection Mapping"],
    "INPUTS": [
        { "NAME": "speed", "LABEL": "Speed", "TYPE": "float", "DEFAULT": 1, "MIN": 0, "MAX": 2 },
        { "NAME": "timeOffset", "LABEL": "Start At (s)", "TYPE": "float", "DEFAULT": 0, "MIN": 0, "MAX": 20 },
        { "NAME": "projector", "LABEL": "Light Only (black background)", "TYPE": "bool", "DEFAULT": true },
        { "NAME": "glow", "LABEL": "Glow", "TYPE": "float", "DEFAULT": 1, "MIN": 0, "MAX": 2 },
        { "NAME": "intensity", "LABEL": "Intensity", "TYPE": "float", "DEFAULT": 1, "MIN": 0, "MAX": 2 }
    ]
}*/

// Icarus Falling as an ISF generator, ported from scenes/icarus_fall.html.
// The numbers below (colors, rest pose, capsules, feather release poses, wax
// sources, the splash point) are baked from that scene's own math.
//
// Every pixel evaluates the pose for the current time, then ray traces the
// figure (18 tapered capsules, front and back faces, with the 1-pixel lattice
// and fresnel rim), 24 feather vanes, the wax drips and spray, the sun and
// the projected sea. Bloom is approximated with distance-based halos.
// World units match the browser scene: the camera frames x -2.25..2.25 and
// y -1.5..1.5 (wider screens show more sky at the sides), the horizon sits
// at y = -1.05.

#define LOOP 20.0
#define PI 3.14159265
#define TAU 6.28318531
#define DEG 0.01745329
#define FIG 0.85
#define CELL 0.042
#define SEA_Y -1.05
#define SUN_R 0.22
#define GLOW_NEAR 0.18
#define GLOW_FAR 0.07
#define IMPACT_T 13.908333
#define IMPACT_X -0.405679

const vec3 C_WALL_TOP = vec3(0.153595, 0.190317, 0.14906);
const vec3 C_WALL_BOT = vec3(0.101801, 0.128006, 0.103459);
const vec3 C_FILL = vec3(0.004421, 0.146538, 0.695559);
const vec3 C_LINE = vec3(0.0, 0.406222, 1.558303);
const vec3 C_RIM = vec3(0.0, 0.962681, 8.139224);
const vec3 C_HOT = vec3(0.0, 4.022515, 13.204039);
const vec3 C_SUN_CORE = vec3(2.590011, 0.810446, 0.0);
const vec3 C_SUN_EDGE = vec3(1.214021, 0.136966, 0.0);
const vec3 C_SUN_HALO = vec3(1.099635, 0.075119, 0.0);
const vec3 C_HEAT = vec3(1.062854, 0.052305, 0.0);
const vec3 C_BURN = vec3(0.765337, 0.003835, 0.005725);
const vec3 C_WAX = vec3(1.540034, 0.285094, 0.010728);
const vec3 C_SEA = vec3(0.0, 0.406222, 1.558303);
const vec3 C_HORIZON = vec3(0.0, 0.962681, 8.139224);
const vec3 C_SEA_FILL = vec3(0.017037, 0.055001, 0.130806);
const vec3 C_SPRAY = vec3(0.0, 4.022515, 13.204039);
const vec3 SUN = vec3(1.32, 0.86, 0.01);
const vec4 Q_FLY = vec4(0.11679, 0.192367, -0.505652, 0.832869);
const vec4 Q_REACH = vec4(0.019704, 0.07671, -0.248, 0.965517);

// ---------------------------------------------------------------- per-pixel state
vec3 gRo, gRd;          // camera ray
float gPxA;             // world size of one pixel per unit of distance
float gT, gSunI;        // loop time, sun intensity
vec3 gFigAll, gFigBest; // every figure surface, and the nearest front one
float gBestT, gTrans;   // nearest front hit, transmittance through the figure
vec3 gFigBloom, gGlow;  // halos
vec3 gFeath;            // feathers, composited over the figure
float gFeathA;
vec3 gDrops;

// ---------------------------------------------------------------- math
float sm(float a, float b, float x) { float u = clamp((x - a) / (b - a), 0.0, 1.0); return u * u * (3.0 - 2.0 * u); }
float easeIO(float u) { return u < 0.5 ? 4.0 * u * u * u : 1.0 - pow(-2.0 * u + 2.0, 3.0) / 2.0; }
float gravityEase(float u) { return u * u * (1.35 - 0.35 * u); }
float flightEase(float u) { return 1.0 - pow(1.0 - u, 1.7); }
float hash1(float n) { return fract(sin(n) * 43758.5453); }

vec4 qmul(vec4 a, vec4 b) {
    return vec4(a.x * b.w + a.w * b.x + a.y * b.z - a.z * b.y,
                a.y * b.w + a.w * b.y + a.z * b.x - a.x * b.z,
                a.z * b.w + a.w * b.z + a.x * b.y - a.y * b.x,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
}
vec3 qrot(vec4 q, vec3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }
vec3 qinv(vec4 q, vec3 v) { return qrot(vec4(-q.xyz, q.w), v); }
vec4 qaa(vec3 axis, float a) { return vec4(axis * sin(a * 0.5), cos(a * 0.5)); }
// Euler angles in degrees, order XYZ (as THREE.Quaternion.setFromEuler)
vec4 qeul(vec3 d) {
    vec3 h = d * (DEG * 0.5), c = cos(h), s = sin(h);
    return vec4(s.x * c.y * c.z + c.x * s.y * s.z,
                c.x * s.y * c.z - s.x * c.y * s.z,
                c.x * c.y * s.z + s.x * s.y * c.z,
                c.x * c.y * c.z - s.x * s.y * s.z);
}
vec4 qslerp(vec4 a, vec4 b, float t) {
    if (t <= 0.0) return a;
    if (t >= 1.0) return b;
    float ch = dot(a, b);
    if (ch < 0.0) { b = -b; ch = -ch; }
    if (ch >= 1.0) return a;
    float ss = 1.0 - ch * ch;
    if (ss <= 1e-6) return normalize(a * (1.0 - t) + b * t);
    float sh = sqrt(ss), ht = atan(sh, ch);
    return a * (sin((1.0 - t) * ht) / sh) + b * (sin(t * ht) / sh);
}

vec3 crSeg(vec3 p0, vec3 p1, vec3 p2, vec3 p3, float u) {
    float u2 = u * u, u3 = u2 * u;
    return 0.5 * (2.0 * p1 + (-p0 + p2) * u + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * u2 + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * u3);
}
// Catmull-Rom through a, b, c, d (as the scene's catmull())
vec3 catmull4(vec3 a, vec3 b, vec3 c, vec3 d, float k) {
    float x = k * 3.0;
    if (x < 1.0) return crSeg(a, a, b, c, x);
    if (x < 2.0) return crSeg(a, b, c, d, x - 1.0);
    return crSeg(b, c, d, d, x - 2.0);
}
vec3 catmull6(vec3 a, vec3 b, vec3 c, vec3 d, vec3 e, vec3 g, float k) {
    float x = k * 5.0;
    if (x < 1.0) return crSeg(a, a, b, c, x);
    if (x < 2.0) return crSeg(a, b, c, d, x - 1.0);
    if (x < 3.0) return crSeg(b, c, d, e, x - 2.0);
    if (x < 4.0) return crSeg(c, d, e, g, x - 3.0);
    return crSeg(d, e, g, g, x - 4.0);
}

// ---------------------------------------------------------------- animation
vec3 rootPos(float t, vec3 offL) {
    vec3 v = offL;
    if (t >= 0.15) { float u = clamp((t - 0.15) / 5.95, 0.0, 1.0); v = u >= 1.0 ? vec3(0.4, 0.22, 0.33) : catmull4(offL, vec3(-1.85, -0.72, 0.32), vec3(-0.55, -0.12, 0.36), vec3(0.4, 0.22, 0.33), flightEase(u)); }
    if (t >= 6.1) { float u = clamp((t - 6.1) / 2.2, 0.0, 1.0); v = u >= 1.0 ? vec3(0.55, 0.3, 0.3) : mix(v, vec3(0.55, 0.3, 0.3), easeIO(u)); }
    if (t >= 8.45) { float u = clamp((t - 8.45) / 6.85, 0.0, 1.0); v = u >= 1.0 ? vec3(-0.55, -2.4, 0.4) : catmull6(vec3(0.55, 0.3, 0.3), vec3(0.42, 0.34, 0.36), vec3(0.08, 0.05, 0.55), vec3(-0.32, -0.45, 0.68), vec3(-0.48, -1.3, 0.5), vec3(-0.55, -2.4, 0.4), gravityEase(u)); }
    if (t >= 19.95) v = offL;
    return v;
}
// wingbeat phase: the exact integral of a frequency ramp from 1.15 to 1.6 Hz
float flapPhase(float t) {
    float a = 6.4, L = 1.8, u = clamp((t - a) / L, 0.0, 1.0);
    float ramped = L * (u * u * u - u * u * u * u / 2.0) + max(0.0, t - a - L);
    float ragged = t > 8.2 ? 0.35 * sin((t - 8.2) * 5.0) * sm(8.2, 8.6, t) : 0.0;
    return TAU * (1.15 * t + 0.45 * ramped) + ragged;
}
vec4 rootQuat(float t) {
    vec4 q = qslerp(Q_FLY, Q_REACH, easeIO(clamp((t - 6.0) / 1.4, 0.0, 1.0)));
    if (t > 8.2) {
        float tau = t - 8.2;
        float spin = 3.0 * PI * pow(clamp(tau / 6.3, 0.0, 1.0), 1.45);
        float roll = 0.85 * sin(tau * 1.15) * sm(0.0, 1.5, tau) * (1.0 - sm(5.0, 6.2, tau));
        vec4 tumble = qmul(qmul(qaa(vec3(0.0, 0.0, 1.0), spin), Q_REACH), qaa(vec3(0.0, 1.0, 0.0), roll));
        q = qslerp(q, tumble, easeIO(clamp(tau / 0.9, 0.0, 1.0)));
    }
    return q;
}

vec3 P_pelvis; vec4 Q_pelvis;
vec3 P_spine; vec4 Q_spine;
vec3 P_chest; vec4 Q_chest;
vec3 P_neck; vec4 Q_neck;
vec3 P_head; vec4 Q_head;
vec3 P_shoulder_L; vec4 Q_shoulder_L;
vec3 P_elbow_L; vec4 Q_elbow_L;
vec3 P_wrist_L; vec4 Q_wrist_L;
vec3 P_hand_L; vec4 Q_hand_L;
vec3 P_shoulder_R; vec4 Q_shoulder_R;
vec3 P_elbow_R; vec4 Q_elbow_R;
vec3 P_wrist_R; vec4 Q_wrist_R;
vec3 P_hand_R; vec4 Q_hand_R;
vec3 P_hip_L; vec4 Q_hip_L;
vec3 P_knee_L; vec4 Q_knee_L;
vec3 P_ankle_L; vec4 Q_ankle_L;
vec3 P_toe_L; vec4 Q_toe_L;
vec3 P_hip_R; vec4 Q_hip_R;
vec3 P_knee_R; vec4 Q_knee_R;
vec3 P_ankle_R; vec4 Q_ankle_R;
vec3 P_toe_R; vec4 Q_toe_R;

// w = (flap, reach, panic, flail), dive returned separately
void pose(float t, vec3 offL, out float ph, out vec4 w) {
    float flap = sm(0.3, 1.0, t) * (1.0 - sm(8.4, 9.3, t));
    float reach = sm(6.1, 7.1, t) * (1.0 - sm(8.0, 8.7, t));
    float panic = sm(7.9, 8.7, t) * (1.0 - sm(9.0, 9.6, t));
    float flail = sm(8.6, 9.8, t) * (1.0 - sm(13.4, 14.3, t));
    float dive = sm(13.5, 14.4, t);
    w = vec4(flap, reach, panic, flail);
    ph = flapPhase(t);
    float amp = 36.0 + 14.0 * panic;
    float beat = sin(ph) + 0.22 * sin(2.0 * ph);
    // flight: the elbow and wrist lag the shoulder
    vec3 A_shoulder_L = vec3(8.0 * sin(ph - 0.3), 0.0, amp * beat) * flap;
    vec3 A_shoulder_R = vec3(8.0 * sin(ph - 0.3), 0.0, -amp * beat) * flap;
    vec3 A_elbow_L = vec3(0.0, -6.0, 16.0 * sin(ph - 0.7)) * flap;
    vec3 A_elbow_R = vec3(0.0, 6.0, -16.0 * sin(ph - 0.7)) * flap;
    vec3 A_wrist_L = vec3(0.0, 0.0, 20.0 * sin(ph - 1.25)) * flap;
    vec3 A_wrist_R = vec3(0.0, 0.0, -20.0 * sin(ph - 1.25)) * flap;
    vec3 A_hip_L = vec3(-6.0 * sin(ph - 1.9), 0.0, 3.0) * flap;
    vec3 A_hip_R = vec3(-6.0 * sin(ph - 1.4), 0.0, -3.0) * flap;
    vec3 A_knee_L = vec3(10.0 + 7.0 * sin(ph - 2.4), 0.0, 0.0) * flap;
    vec3 A_knee_R = A_knee_L;
    vec3 A_neck = vec3(-18.0, 0.0, 0.0) * flap;
    vec3 A_spine = vec3(-6.0 * sin(ph - 0.9), 0.0, 0.0) * flap;
    vec3 A_chest = vec3(0.0);
    // reaching for the sun
    A_shoulder_L += vec3(0.0, -20.0, 48.0) * reach; A_elbow_L += vec3(0.0, 0.0, 12.0) * reach;
    A_shoulder_R += vec3(0.0, 20.0, -22.0) * reach;
    A_neck += vec3(-16.0, 0.0, 6.0) * reach;
    // the fall: limbs flail on unrelated rhythms
    float T2 = TAU * t;
    A_shoulder_L += vec3(32.0 * sin(T2 * 0.62 + 1.0), 0.0, 70.0 * sin(T2 * 0.9)) * flail;
    A_elbow_L += vec3(0.0, 0.0, 40.0 + 38.0 * sin(T2 * 1.25)) * flail;
    A_wrist_L += vec3(0.0, 0.0, 25.0 * sin(T2 * 1.6 - 0.6)) * flail;
    A_shoulder_R += vec3(32.0 * sin(T2 * 0.66 + 2.4), 0.0, -70.0 * sin(T2 * 0.84 + 1.7)) * flail;
    A_elbow_R += vec3(0.0, 0.0, -(40.0 + 38.0 * sin(T2 * 1.18 + 1.6))) * flail;
    A_wrist_R += vec3(0.0, 0.0, -25.0 * sin(T2 * 1.5 + 1.1)) * flail;
    A_hip_L += vec3(-38.0 * sin(T2 * 0.74 + 0.3), 0.0, 12.0 * sin(T2 * 0.5 + 1.1)) * flail;
    A_knee_L += vec3(34.0 + 32.0 * sin(T2 * 1.08 + 0.4), 0.0, 0.0) * flail;
    A_hip_R += vec3(-38.0 * sin(T2 * 0.8 + 2.0), 0.0, -12.0 * sin(T2 * 0.55 + 2.6)) * flail;
    A_knee_R += vec3(34.0 + 32.0 * sin(T2 * 1.02 + 2.1), 0.0, 0.0) * flail;
    A_spine += vec3(14.0 * sin(T2 * 0.5 + 0.2), 8.0 * sin(T2 * 0.42 + 1.0), 9.0 * sin(T2 * 0.6 + 2.0)) * flail;
    A_chest += vec3(8.0 * sin(T2 * 0.5 - 0.2), 0.0, 6.0 * sin(T2 * 0.6 + 1.6)) * flail;
    A_neck += vec3(26.0 * sin(T2 * 0.7 + 0.5), 18.0 * sin(T2 * 0.45 + 2.2), 0.0) * flail;
    // the dive: arms over the head, legs together
    A_shoulder_L += vec3(0.0, 0.0, 150.0) * dive; A_shoulder_R += vec3(0.0, 0.0, -150.0) * dive;
    A_elbow_L += vec3(0.0, 0.0, -8.0) * dive; A_elbow_R += vec3(0.0, 0.0, 8.0) * dive;
    A_hip_L += vec3(0.0, 0.0, -3.0) * dive; A_hip_R += vec3(0.0, 0.0, 3.0) * dive;
    A_neck += vec3(12.0, 0.0, 0.0) * dive;

    vec3 rootP = rootPos(t, offL);
    rootP.y += 0.035 * sin(ph + PI) * flap;   // the body lifts on each downstroke
    vec4 rootQ = rootQuat(t);
    P_pelvis = rootP; Q_pelvis = rootQ;
    P_spine = P_pelvis + qrot(Q_pelvis, vec3(0.0, 0.102, 0.0));
    Q_spine = qmul(Q_pelvis, qeul(A_spine));
    P_chest = P_spine + qrot(Q_spine, vec3(0.0, 0.187, 0.0));
    Q_chest = qmul(Q_spine, qeul(A_chest));
    P_neck = P_chest + qrot(Q_chest, vec3(0.0, 0.187, 0.0));
    Q_neck = qmul(Q_chest, qeul(A_neck));
    P_head = P_neck + qrot(Q_neck, vec3(0.0, 0.0765, 0.0));
    Q_head = Q_neck;
    P_shoulder_L = P_chest + qrot(Q_chest, vec3(0.153, 0.1275, 0.0));
    Q_shoulder_L = qmul(Q_chest, qeul(A_shoulder_L));
    P_elbow_L = P_shoulder_L + qrot(Q_shoulder_L, vec3(0.255, 0.0, 0.0));
    Q_elbow_L = qmul(Q_shoulder_L, qeul(A_elbow_L));
    P_wrist_L = P_elbow_L + qrot(Q_elbow_L, vec3(0.221, 0.0, 0.0));
    Q_wrist_L = qmul(Q_elbow_L, qeul(A_wrist_L));
    P_hand_L = P_wrist_L + qrot(Q_wrist_L, vec3(0.1445, 0.0, 0.0));
    Q_hand_L = Q_wrist_L;
    P_shoulder_R = P_chest + qrot(Q_chest, vec3(-0.153, 0.1275, 0.0));
    Q_shoulder_R = qmul(Q_chest, qeul(A_shoulder_R));
    P_elbow_R = P_shoulder_R + qrot(Q_shoulder_R, vec3(-0.255, 0.0, 0.0));
    Q_elbow_R = qmul(Q_shoulder_R, qeul(A_elbow_R));
    P_wrist_R = P_elbow_R + qrot(Q_elbow_R, vec3(-0.221, 0.0, 0.0));
    Q_wrist_R = qmul(Q_elbow_R, qeul(A_wrist_R));
    P_hand_R = P_wrist_R + qrot(Q_wrist_R, vec3(-0.1445, 0.0, 0.0));
    Q_hand_R = Q_wrist_R;
    P_hip_L = P_pelvis + qrot(Q_pelvis, vec3(0.085, -0.0425, 0.0));
    Q_hip_L = qmul(Q_pelvis, qeul(A_hip_L));
    P_knee_L = P_hip_L + qrot(Q_hip_L, vec3(0.0, -0.374, 0.0));
    Q_knee_L = qmul(Q_hip_L, qeul(A_knee_L));
    P_ankle_L = P_knee_L + qrot(Q_knee_L, vec3(0.0, -0.3655, 0.0));
    Q_ankle_L = Q_knee_L;
    P_toe_L = P_ankle_L + qrot(Q_ankle_L, vec3(0.0, -0.0425, 0.1275));
    Q_toe_L = Q_ankle_L;
    P_hip_R = P_pelvis + qrot(Q_pelvis, vec3(-0.085, -0.0425, 0.0));
    Q_hip_R = qmul(Q_pelvis, qeul(A_hip_R));
    P_knee_R = P_hip_R + qrot(Q_hip_R, vec3(0.0, -0.374, 0.0));
    Q_knee_R = qmul(Q_hip_R, qeul(A_knee_R));
    P_ankle_R = P_knee_R + qrot(Q_knee_R, vec3(0.0, -0.3655, 0.0));
    Q_ankle_R = Q_knee_R;
    P_toe_R = P_ankle_R + qrot(Q_ankle_R, vec3(0.0, -0.0425, 0.1275));
    Q_toe_R = Q_ankle_R;
}

// ---------------------------------------------------------------- baked tables
// r = (radius at A, radius at B, cross-section flatness, lattice v0), m = (length / FIG, lattice around, tip glow)
void capData(int k, out vec4 r, out vec3 m) {
    if (k < 9) {
        if (k < 4) {
            if (k < 2) {
                if (k < 1) {
                    r = vec4(0.09775, 0.09775, 0.72, 0.0); m = vec3(0.24, 0.714, 0.0);
                } else {
                    r = vec4(0.0935, 0.09775, 0.7, 0.0); m = vec3(0.1, 0.714, 0.0);
                }
            } else {
                if (k < 3) {
                    r = vec4(0.102, 0.1275, 0.66, 0.12); m = vec3(0.28, 0.84, 0.0);
                } else {
                    r = vec4(0.06375, 0.06375, 0.8, 0.0); m = vec3(0.3, 0.462, 0.0);
                }
            }
        } else {
            if (k < 6) {
                if (k < 5) {
                    r = vec4(0.0425, 0.03825, 1.0, 0.5); m = vec3(0.12, 0.336, 0.0);
                } else {
                    r = vec4(0.08075, 0.07225, 1.0, 0.6); m = vec3(0.090554, 0.546, 0.7);
                }
            } else {
                if (k < 7) {
                    r = vec4(0.04675, 0.0374, 1.0, 0.0); m = vec3(0.3, 0.336, 0.0);
                } else {
                    if (k < 8) {
                        r = vec4(0.03655, 0.0272, 1.0, 0.3); m = vec3(0.26, 0.336, 0.0);
                    } else {
                        r = vec4(0.0255, 0.0204, 0.5, 0.56); m = vec3(0.17, 0.336, 1.0);
                    }
                }
            }
        }
    } else {
        if (k < 13) {
            if (k < 11) {
                if (k < 10) {
                    r = vec4(0.07225, 0.0493, 1.0, 0.0); m = vec3(0.44, 0.462, 0.0);
                } else {
                    r = vec4(0.04675, 0.0323, 1.0, 0.44); m = vec3(0.43, 0.336, 0.0);
                }
            } else {
                if (k < 12) {
                    r = vec4(0.0323, 0.0238, 0.7, 0.87); m = vec3(0.158114, 0.336, 1.0);
                } else {
                    r = vec4(0.04675, 0.0374, 1.0, 0.0); m = vec3(0.3, 0.336, 0.0);
                }
            }
        } else {
            if (k < 15) {
                if (k < 14) {
                    r = vec4(0.03655, 0.0272, 1.0, 0.3); m = vec3(0.26, 0.336, 0.0);
                } else {
                    r = vec4(0.0255, 0.0204, 0.5, 0.56); m = vec3(0.17, 0.336, 1.0);
                }
            } else {
                if (k < 16) {
                    r = vec4(0.07225, 0.0493, 1.0, 0.0); m = vec3(0.44, 0.462, 0.0);
                } else {
                    if (k < 17) {
                        r = vec4(0.04675, 0.0323, 1.0, 0.44); m = vec3(0.43, 0.336, 0.0);
                    } else {
                        r = vec4(0.0323, 0.0238, 0.7, 0.87); m = vec3(0.158114, 0.336, 1.0);
                    }
                }
            }
        }
    }
}
// a = (position along the bone, length, fan angle, release time), b = (burn time, drift vx, vy, spin)
// SP, SQ = pose at release
void featherData(int k, out vec4 a, out vec4 b, out vec3 SP, out vec4 SQ) {
    if (k < 12) {
        if (k < 6) {
            if (k < 3) {
                if (k < 1) {
                    a = vec4(0.15, 0.255, 100.0, 99.0); b = vec4(2.284997, 0.157185, 0.187395, -0.979924);
                    SP = vec3(0.0, 0.0, 0.0); SQ = vec4(0.0, 0.0, 0.0, 1.0);
                } else {
                    if (k < 2) {
                        a = vec4(0.45, 0.2805, 98.0, 99.0); b = vec4(2.151233, -0.291137, 0.28772, 1.71382);
                        SP = vec3(0.0, 0.0, 0.0); SQ = vec4(0.0, 0.0, 0.0, 1.0);
                    } else {
                        a = vec4(0.75, 0.306, 96.0, 8.953695); b = vec4(2.328269, 0.099193, 0.447806, -3.231696);
                        SP = vec3(0.8993, 0.696092, 0.146608); SQ = vec4(-0.032333, 0.353829, -0.029412, 0.934288);
                    }
                }
            } else {
                if (k < 4) {
                    a = vec4(0.05, 0.391, 94.0, 8.641791); b = vec4(2.049547, -0.262177, 0.229691, 3.418812);
                    SP = vec3(1.06306, 0.484351, 0.214666); SQ = vec4(0.007223, 0.172617, -0.340888, 0.924092);
                } else {
                    if (k < 5) {
                        a = vec4(0.3, 0.425, 90.0, 8.505449); b = vec4(1.909344, -0.320479, 0.336468, -3.140844);
                        SP = vec3(1.187412, 0.619239, 0.22896); SQ = vec4(0.019182, 0.108118, 0.118068, 0.986916);
                    } else {
                        a = vec4(0.55, 0.4505, 86.0, 8.168401); b = vec4(99.0, 0.16237, 0.354243, -1.353005);
                        SP = vec3(1.205198, 0.436001, 0.366628); SQ = vec4(-0.038931, 0.014268, -0.200345, 0.978848);
                    }
                }
            }
        } else {
            if (k < 9) {
                if (k < 7) {
                    a = vec4(0.8, 0.476, 81.0, 8.18098); b = vec4(99.0, -0.247999, 0.321446, 2.697746);
                    SP = vec3(1.241264, 0.39007, 0.380113); SQ = vec4(-0.040605, 0.018852, -0.181545, 0.982363);
                } else {
                    if (k < 8) {
                        a = vec4(0.05, 0.527, 74.0, 7.927548); b = vec4(1.168923, 0.042996, 0.168929, -2.593055);
                        SP = vec3(1.195129, 0.909077, 0.290866); SQ = vec4(-0.185305, 0.007194, 0.672263, 0.716709);
                    } else {
                        a = vec4(0.3, 0.561, 64.0, 7.656816); b = vec4(2.162492, -0.298737, 0.169935, 0.44493);
                        SP = vec3(1.362229, 0.699875, 0.417293); SQ = vec4(-0.078853, -0.012386, 0.218965, 0.972462);
                    }
                }
            } else {
                if (k < 10) {
                    a = vec4(0.55, 0.595, 52.0, 7.508184); b = vec4(99.0, -0.141082, 0.2518, -1.462077);
                    SP = vec3(1.369656, 0.440995, 0.432339); SQ = vec4(-0.118325, 0.017022, 0.118831, 0.985692);
                } else {
                    if (k < 11) {
                        a = vec4(0.8, 0.612, 40.0, 7.180504); b = vec4(1.094236, 0.11048, 0.371751, 0.385827);
                        SP = vec3(1.041983, 1.027299, 0.313068); SQ = vec4(-0.22527, -0.013619, 0.888553, 0.399425);
                    } else {
                        a = vec4(1.0, 0.595, 28.0, 6.907242); b = vec4(2.323306, -0.232271, 0.163957, -0.181453);
                        SP = vec3(1.386688, 0.385776, 0.431131); SQ = vec4(-0.092972, 0.025259, 0.311413, 0.945378);
                    }
                }
            }
        }
    } else {
        if (k < 18) {
            if (k < 15) {
                if (k < 13) {
                    a = vec4(0.15, 0.255, 100.0, 99.0); b = vec4(1.278729, -0.064479, 0.276004, 1.603897);
                    SP = vec3(0.0, 0.0, 0.0); SQ = vec4(0.0, 0.0, 0.0, 1.0);
                } else {
                    if (k < 14) {
                        a = vec4(0.45, 0.2805, 98.0, 99.0); b = vec4(2.019443, -0.036547, 0.410691, 0.973046);
                        SP = vec3(0.0, 0.0, 0.0); SQ = vec4(0.0, 0.0, 0.0, 1.0);
                    } else {
                        a = vec4(0.75, 0.306, 96.0, 8.759999); b = vec4(99.0, -0.088085, 0.163415, -0.331624);
                        SP = vec3(0.353774, 0.700671, 0.400583); SQ = vec4(0.077755, 0.004869, 0.169116, 0.982512);
                    }
                }
            } else {
                if (k < 16) {
                    a = vec4(0.05, 0.391, 94.0, 8.932148); b = vec4(1.550882, -0.327754, 0.495941, -1.959863);
                    SP = vec3(0.223903, 0.693228, 0.474913); SQ = vec4(0.116507, 0.096057, 0.086694, 0.984725);
                } else {
                    if (k < 17) {
                        a = vec4(0.3, 0.425, 90.0, 8.352596); b = vec4(2.191223, -0.326174, 0.167958, 0.7152);
                        SP = vec3(0.535248, 1.011772, 0.432156); SQ = vec4(-0.031228, 0.110871, -0.589496, 0.799517);
                    } else {
                        a = vec4(0.55, 0.4505, 86.0, 8.076112); b = vec4(1.318764, -0.197528, 0.25735, -3.093646);
                        SP = vec3(0.338428, 0.929984, 0.512649); SQ = vec4(-0.000834, 0.179585, -0.260696, 0.948571);
                    }
                }
            }
        } else {
            if (k < 21) {
                if (k < 19) {
                    a = vec4(0.8, 0.476, 81.0, 8.340779); b = vec4(1.745873, -0.009035, 0.459887, -0.248042);
                    SP = vec3(0.459853, 1.092456, 0.488232); SQ = vec4(-0.042909, 0.119176, -0.596943, 0.792222);
                } else {
                    if (k < 20) {
                        a = vec4(0.05, 0.527, 74.0, 7.712228); b = vec4(99.0, -0.335567, 0.195347, -3.377897);
                        SP = vec3(0.489256, 1.128492, 0.55909); SQ = vec4(-0.041284, 0.195822, -0.566596, 0.799324);
                    } else {
                        a = vec4(0.3, 0.561, 64.0, 7.785041); b = vec4(1.685783, -0.159494, 0.347246, -1.261883);
                        SP = vec3(0.671034, 1.187523, 0.488902); SQ = vec4(-0.16962, 0.217211, -0.810752, 0.516459);
                    }
                }
            } else {
                if (k < 22) {
                    a = vec4(0.55, 0.595, 52.0, 7.225126); b = vec4(99.0, 0.052157, 0.251896, 3.238145);
                    SP = vec3(0.506891, 1.179313, 0.506086); SQ = vec4(-0.257523, 0.20791, -0.863319, 0.380967);
                } else {
                    if (k < 23) {
                        a = vec4(0.8, 0.612, 40.0, 7.1646); b = vec4(1.079738, -0.336419, 0.344291, -2.118589);
                        SP = vec3(0.611217, 1.207283, 0.502242); SQ = vec4(-0.278873, 0.214567, -0.906769, 0.232296);
                    } else {
                        a = vec4(1.0, 0.595, 28.0, 7.19798); b = vec4(99.0, 0.186121, 0.315927, -3.007944);
                        SP = vec3(0.573672, 1.238014, 0.512871); SQ = vec4(-0.314666, 0.200239, -0.917467, 0.138365);
                    }
                }
            }
        }
    }
}
void waxData(int k, out vec3 src, out vec2 d) {
    if (k < 9) {
        if (k < 4) {
            if (k < 2) {
                if (k < 1) {
                    src = vec3(0.174399, 0.71893, 0.633565); d = vec2(6.775003, -0.002002);
                } else {
                    src = vec3(1.241547, 0.344848, 0.393899); d = vec2(6.879199, -0.058539);
                }
            } else {
                if (k < 3) {
                    src = vec3(0.417436, 1.056771, 0.600065); d = vec2(6.986584, 0.06269);
                } else {
                    src = vec3(1.016702, 0.918742, 0.310955); d = vec2(7.165076, 0.114838);
                }
            }
        } else {
            if (k < 6) {
                if (k < 5) {
                    src = vec3(0.359264, 1.079192, 0.523214); d = vec2(7.315935, 0.031109);
                } else {
                    src = vec3(1.306029, 0.662805, 0.355339); d = vec2(7.412444, -0.078076);
                }
            } else {
                if (k < 7) {
                    src = vec3(0.191459, 0.805799, 0.596282); d = vec2(7.498769, 0.07969);
                } else {
                    if (k < 8) {
                        src = vec3(1.261507, 0.833783, 0.385655); d = vec2(7.691876, 0.007527);
                    } else {
                        src = vec3(0.669913, 1.146985, 0.467604); d = vec2(7.793011, -0.06769);
                    }
                }
            }
        }
    } else {
        if (k < 13) {
            if (k < 11) {
                if (k < 10) {
                    src = vec3(1.140905, 0.936223, 0.290802); d = vec2(7.892924, 0.101719);
                } else {
                    src = vec3(0.247305, 0.931911, 0.574014); d = vec2(8.090688, -0.092416);
                }
            } else {
                if (k < 12) {
                    src = vec3(1.313107, 0.439549, 0.387371); d = vec2(8.143033, 0.060413);
                } else {
                    src = vec3(0.412881, 1.113942, 0.514007); d = vec2(8.334165, -0.10299);
                }
            }
        } else {
            if (k < 15) {
                if (k < 14) {
                    src = vec3(1.222828, 0.885005, 0.324085); d = vec2(8.398804, 0.026253);
                } else {
                    src = vec3(0.207562, 0.991973, 0.416124); d = vec2(8.580501, -0.024832);
                }
            } else {
                if (k < 16) {
                    src = vec3(1.159855, 0.317951, 0.20029); d = vec2(8.690067, -0.042411);
                } else {
                    if (k < 17) {
                        src = vec3(0.100761, 0.546135, 0.488045); d = vec2(8.813036, -0.045032);
                    } else {
                        src = vec3(1.162016, 0.703528, 0.033201); d = vec2(8.941987, 0.063828);
                    }
                }
            }
        }
    }
}
void sprayData(int k, out vec4 a, out float size) {
    if (k < 23) {
        if (k < 11) {
            if (k < 5) {
                if (k < 2) {
                    if (k < 1) {
                        a = vec4(0.035501, 3.146005, 0.122391, 0.105829); size = 0.041564;
                    } else {
                        a = vec4(0.166949, 1.778332, 0.155286, 0.118066); size = 0.03682;
                    }
                } else {
                    if (k < 3) {
                        a = vec4(0.360267, 1.372549, 0.055364, 0.045761); size = 0.037637;
                    } else {
                        if (k < 4) {
                            a = vec4(-0.445039, 1.645181, -0.156836, 0.06553); size = 0.036594;
                        } else {
                            a = vec4(0.695281, 3.035404, -0.175578, 0.069722); size = 0.026987;
                        }
                    }
                }
            } else {
                if (k < 8) {
                    if (k < 6) {
                        a = vec4(-0.276009, 1.441553, -0.000686, 0.00463); size = 0.046097;
                    } else {
                        if (k < 7) {
                            a = vec4(0.040613, 1.646149, 0.151057, 0.060331); size = 0.028243;
                        } else {
                            a = vec4(0.344461, 2.35704, -0.027254, 0.046682); size = 0.04275;
                        }
                    }
                } else {
                    if (k < 9) {
                        a = vec4(0.716267, 2.565686, -0.141883, 0.087801); size = 0.023817;
                    } else {
                        if (k < 10) {
                            a = vec4(0.484486, 1.825764, -0.149343, 0.06827); size = 0.04392;
                        } else {
                            a = vec4(0.497337, 3.134345, 0.102368, 0.105347); size = 0.040746;
                        }
                    }
                }
            }
        } else {
            if (k < 17) {
                if (k < 14) {
                    if (k < 12) {
                        a = vec4(-0.414445, 3.331718, 0.137332, 0.079799); size = 0.032907;
                    } else {
                        if (k < 13) {
                            a = vec4(-0.684967, 2.732183, -0.069254, 0.0842); size = 0.047404;
                        } else {
                            a = vec4(-0.503213, 1.458982, -0.012772, 0.078582); size = 0.023907;
                        }
                    }
                } else {
                    if (k < 15) {
                        a = vec4(0.423602, 1.34653, -0.000794, 0.026853); size = 0.023734;
                    } else {
                        if (k < 16) {
                            a = vec4(-0.638965, 2.395886, -0.186771, 0.12975); size = 0.033184;
                        } else {
                            a = vec4(-0.502567, 3.306342, -0.027626, 0.033618); size = 0.036938;
                        }
                    }
                }
            } else {
                if (k < 20) {
                    if (k < 18) {
                        a = vec4(-0.02864, 2.236475, -0.066623, 0.111733); size = 0.024249;
                    } else {
                        if (k < 19) {
                            a = vec4(0.110457, 1.548702, 0.05086, 0.009783); size = 0.032615;
                        } else {
                            a = vec4(-0.382824, 1.503944, -0.004943, 0.118033); size = 0.025908;
                        }
                    }
                } else {
                    if (k < 21) {
                        a = vec4(0.489564, 3.014209, 0.100991, 0.100939); size = 0.02756;
                    } else {
                        if (k < 22) {
                            a = vec4(0.621145, 3.223995, 0.027996, 0.058728); size = 0.026046;
                        } else {
                            a = vec4(0.221455, 1.984475, 0.040571, 0.136789); size = 0.029317;
                        }
                    }
                }
            }
        }
    } else {
        if (k < 34) {
            if (k < 28) {
                if (k < 25) {
                    if (k < 24) {
                        a = vec4(-0.627345, 2.323371, 0.024519, 0.010268); size = 0.035159;
                    } else {
                        a = vec4(-0.702313, 2.395234, -0.123276, 0.009336); size = 0.037501;
                    }
                } else {
                    if (k < 26) {
                        a = vec4(-0.002742, 1.279375, 0.170716, 0.060224); size = 0.040105;
                    } else {
                        if (k < 27) {
                            a = vec4(0.604228, 2.234246, -0.10395, 0.080438); size = 0.038066;
                        } else {
                            a = vec4(-0.109016, 2.000262, 0.119237, 0.01211); size = 0.023325;
                        }
                    }
                }
            } else {
                if (k < 31) {
                    if (k < 29) {
                        a = vec4(-0.567532, 2.70516, -0.172049, 0.057156); size = 0.034636;
                    } else {
                        if (k < 30) {
                            a = vec4(-0.597371, 2.370342, 0.137238, 0.021041); size = 0.03934;
                        } else {
                            a = vec4(0.409473, 3.005944, 0.088398, 0.029936); size = 0.038992;
                        }
                    }
                } else {
                    if (k < 32) {
                        a = vec4(0.54933, 2.567979, -0.032207, 0.021786); size = 0.037833;
                    } else {
                        if (k < 33) {
                            a = vec4(-0.277017, 2.643424, 0.190827, 0.0394); size = 0.046086;
                        } else {
                            a = vec4(-0.051086, 2.547116, -0.170664, 0.070856); size = 0.046572;
                        }
                    }
                }
            }
        } else {
            if (k < 40) {
                if (k < 37) {
                    if (k < 35) {
                        a = vec4(-0.003177, 1.660343, -0.173325, 0.083469); size = 0.022849;
                    } else {
                        if (k < 36) {
                            a = vec4(-0.747083, 3.424293, -0.027932, 0.097488); size = 0.047856;
                        } else {
                            a = vec4(-0.110503, 1.776301, 0.029821, 0.086511); size = 0.036914;
                        }
                    }
                } else {
                    if (k < 38) {
                        a = vec4(-0.266492, 3.11542, -0.165399, 0.007136); size = 0.046122;
                    } else {
                        if (k < 39) {
                            a = vec4(0.20344, 1.367704, -0.036697, 0.068037); size = 0.030683;
                        } else {
                            a = vec4(-0.019772, 3.223426, -0.139883, 0.09337); size = 0.045792;
                        }
                    }
                }
            } else {
                if (k < 43) {
                    if (k < 41) {
                        a = vec4(0.403963, 2.930388, -0.114469, 0.091496); size = 0.040334;
                    } else {
                        if (k < 42) {
                            a = vec4(0.111986, 2.20676, -0.137755, 0.085256); size = 0.02415;
                        } else {
                            a = vec4(0.162282, 3.544961, -0.08743, 0.129694); size = 0.02429;
                        }
                    }
                } else {
                    if (k < 44) {
                        a = vec4(0.098077, 1.376018, 0.002445, 0.132313); size = 0.047841;
                    } else {
                        if (k < 45) {
                            a = vec4(-0.493105, 1.360049, 0.038484, 0.004569); size = 0.024602;
                        } else {
                            a = vec4(0.682862, 2.232411, 0.078537, 0.139223); size = 0.031091;
                        }
                    }
                }
            }
        }
    }
}

// ---------------------------------------------------------------- figure
// iq's ray / rounded cone intersection, cone from the origin to pb
float iRoundCone(vec3 ro, vec3 rd, vec3 pb, float ra, float rb, out vec3 n) {
    vec3 ob = ro - pb;
    float rr = ra - rb;
    float m0 = dot(pb, pb), m1 = dot(pb, ro), m2 = dot(pb, rd), m3 = dot(rd, ro), m5 = dot(ro, ro), m6 = dot(ob, rd), m7 = dot(ob, ob);
    float d2 = m0 - rr * rr;
    float k2 = d2 - m2 * m2, k1 = d2 * m3 - m1 * m2 + m2 * rr * ra, k0 = d2 * m5 - m1 * m1 + m1 * rr * ra * 2.0 - m0 * ra * ra;
    float h = k1 * k1 - k0 * k2;
    n = vec3(0.0, 0.0, 1.0);
    if (h < 0.0) return -1.0;
    float t = (-sqrt(h) - k1) / k2;
    float y = m1 - ra * rr + t * m2;
    if (y > 0.0 && y < d2) { n = normalize(d2 * (ro + t * rd) - pb * y); return t; }
    float h1 = m3 * m3 - m5 + ra * ra, h2 = m6 * m6 - m7 + rb * rb;
    if (max(h1, h2) < 0.0) return -1.0;
    float r = 1e20;
    if (h1 > 0.0) { r = -m3 - sqrt(h1); n = (ro + r * rd) / ra; }
    if (h2 > 0.0) { t = -m6 - sqrt(h2); if (t < r) { r = t; n = (ob + t * rd) / rb; } }
    return r;
}

// One face of the hologram skin: fill, lattice lines and dots, fresnel rim
// (heated orange near the sun), as premultiplied color + alpha.
vec4 holo(vec3 W, vec3 n, vec2 lat, float r, float around, float tipK, float front, float tw, out vec3 bloom) {
    float ndv = abs(dot(n, gRd));
    float fp = 1.2 * tw * gPxA / max(ndv, 0.2);
    vec2 fw = vec2(fp * around / (TAU * r), fp / FIG) / CELL;
    vec2 g = lat / CELL;
    vec2 dl = abs(fract(g - 0.5) - 0.5) / max(fw, vec2(1e-6));
    float density = smoothstep(2.5, 5.0, 1.0 / max(fw.x, fw.y));
    float lineA = clamp(1.0 - min(dl.x, dl.y) + 0.35, 0.0, 1.0) * density;
    float dotA = clamp(1.9 - length(dl), 0.0, 1.0) * density;
    float fres = pow(1.0 - clamp(ndv, 0.0, 1.0), 3.5);
    vec3 rimCol = mix(C_RIM, C_HOT, clamp(tipK * (0.4 + fres), 0.0, 1.0));
    float heat = clamp(exp(-length(W.xy - SUN.xy) / 0.75) * 1.6 * (gSunI - 0.4), 0.0, 1.0);
    rimCol = mix(rimCol, C_HEAT * 1.4, heat);
    float scan = 1.0 + 0.025 * smoothstep(0.6, 1.0, sin(lat.y * 9.0 - gT * 1.6));
    float face = mix(0.35, 1.0, front);
    float keep = smoothstep(SEA_Y - 0.015, SEA_Y + 0.02, W.y) * clamp(W.z / (tw * gPxA) + 0.5, 0.0, 1.0);   // the sea cuts him off; the wall hides what swings behind it
    vec3 lit = (C_LINE * lineA * 0.85 + C_HOT * dotA * 0.5 + rimCol * fres * 1.05) * face;
    bloom = (C_LINE * lineA * 0.05 + C_HOT * dotA * 0.08 + rimCol * fres * fres * 0.5 + C_HEAT * heat * fres * 0.4) * front * scan * keep;
    return vec4((C_FILL * 0.55 * face + lit) * scan * keep, 0.55 * face * keep);
}

void capsule(vec3 A, vec3 B, vec3 U, vec3 V, float rA, float rB, float fl, float v0, float lenF, float around, float tip) {
    vec3 ax = B - A;
    float len = length(ax);
    ax /= len;
    // ray in the capsule's frame, squashed so the cross-section is round
    vec3 o = gRo - A;
    vec3 roL = vec3(dot(o, U), dot(o, V) / fl, dot(o, ax));
    vec3 rdL = vec3(dot(gRd, U), dot(gRd, V) / fl, dot(gRd, ax));
    float m = length(rdL);
    rdL /= m;
    // closest approach to the axis: the halo, and a cheap early out
    float b = rdL.z, d = dot(rdL, roL);
    float sg = clamp((roL.z - b * d) / max(1.0 - b * b, 1e-5), 0.0, len);
    float tau = b * sg - d;
    float dist = length(roL + rdL * tau - vec3(0.0, 0.0, sg));
    float rs = mix(rA, rB, sg / len);
    float edge = max(dist - rs, 0.0);   // outside the skin
    vec3 cw = A + ax * sg;
    float hz = max(length(cw - gRo), 0.1) * gPxA;   // one pixel, in world units, at this depth
    float heatG = clamp(exp(-length(cw.xy - SUN.xy) / 0.75) * 1.6 * (gSunI - 0.4), 0.0, 1.0);
    vec3 haloCol = mix(C_RIM, C_HEAT * 1.4, heatG) * smoothstep(SEA_Y - 0.015, SEA_Y + 0.02, cw.y) * smoothstep(-0.03, 0.03, cw.z);
    gGlow = max(gGlow, haloCol * (GLOW_NEAR * exp(-abs(dist - rs) / (4.0 * hz)) + GLOW_FAR * exp(-edge / (30.0 * hz))));   // a bright seam on the silhouette, a faint wide spill
    if (dist > max(rA, rB) * 1.02) return;

    vec3 n;
    float tf = iRoundCone(roL, rdL, vec3(0.0, 0.0, len), rA, rB, n);
    if (tf <= 0.0) return;
    vec3 bloom;
    // front face
    vec3 h = roL + rdL * tf;
    float tw = tf / m;
    vec3 nW = normalize(U * n.x + V * (n.y / fl) + ax * n.z);
    float ang = atan(h.y, h.x); if (ang < 0.0) ang += TAU;
    float s = h.z / len;
    float tipK = tip * clamp((s - 0.55) / 0.5, 0.0, 1.0);
    vec4 c = holo(gRo + gRd * tw, nW, vec2(ang / TAU * around, v0 + s * lenF), rs, around, tipK, 1.0, tw, bloom);
    gFigAll += c.rgb; gTrans *= 1.0 - c.a; gFigBloom += bloom;
    if (tw < gBestT && c.a > 0.01) { gBestT = tw; gFigBest = c.rgb; }
    // back face: march in from the far side
    float Tf = dot(vec3(0.0, 0.0, 0.5 * len) - roL, rdL) + len + max(rA, rB);
    float tr = iRoundCone(roL + rdL * Tf, -rdL, vec3(0.0, 0.0, len), rA, rB, n);
    if (tr <= 0.0) return;
    float tb = Tf - tr;
    h = roL + rdL * tb;
    tw = tb / m;
    nW = normalize(U * n.x + V * (n.y / fl) + ax * n.z);
    ang = atan(h.y, h.x); if (ang < 0.0) ang += TAU;
    s = h.z / len;
    tipK = tip * clamp((s - 0.55) / 0.5, 0.0, 1.0);
    c = holo(gRo + gRd * tw, nW, vec2(ang / TAU * around, v0 + s * lenF), rs, around, tipK, 0.0, tw, bloom);
    gFigAll += c.rgb; gTrans *= 1.0 - c.a;
}

// ---------------------------------------------------------------- feathers
float vaneW(float v) { return 0.105 * pow(sin(PI * min(1.0, v * 1.06 + 0.02)), 0.75) * (1.0 - 0.25 * v) + 0.004; }

void vane(vec3 pos, vec4 q, float L, float heat, float alpha) {
    vec3 nW = qrot(q, vec3(0.0, 0.0, 1.0));
    vec3 tipW = pos + qrot(q, vec3(0.0, -L, 0.0));
    vec3 heatCol = mix(C_HEAT, C_BURN, smoothstep(0.55, 1.0, heat));
    // halo around the rachis (burning feathers glow)
    vec3 ab = tipW - pos;
    vec3 ao = gRo - pos;
    float bb = dot(ab, ab), br = dot(ab, gRd), ba = dot(ab, ao), ra = dot(gRd, ao);
    float sg = clamp((ba - br * ra) / max(bb - br * br, 1e-6), 0.0, 1.0);
    vec3 cp = pos + ab * sg;
    float tc = dot(cp - gRo, gRd);
    float dist = length(gRo + gRd * tc - cp);
    float hz = max(tc, 0.1) * gPxA;
    float edge = max(dist - 0.06 * L, 0.0);
    float keepG = smoothstep(SEA_Y - 0.01, SEA_Y + 0.03, cp.y) * smoothstep(-0.03, 0.03, cp.z);
    gGlow += (heatCol * heat * 0.18 + C_RIM * 0.04) * alpha * keepG * (exp(-edge / (6.0 * hz)) * 0.6 + exp(-edge / (30.0 * hz)) * 0.2);
    float dn = dot(gRd, nW);
    // edge-on vanes read as a bright line, as a rasterized vane does
    float edgeOn = (1.0 - smoothstep(0.04, 0.25, abs(dn))) * clamp(1.4 - dist / (1.2 * hz), 0.0, 1.0) * keepG * alpha;
    gFeath += mix(mix(C_RIM, C_HOT, 0.3 * sg), heatCol * 1.5, heat) * edgeOn * 1.1;
    if (dist > 0.16 * L) return;

    if (abs(dn) < 1e-4) return;
    float th = dot(pos - gRo, nW) / dn;
    vec3 l = qinv(q, gRo + gRd * th - pos) / L;
    float v = -l.y;
    float wv = vaneW(clamp(v, 0.0, 1.0));
    float u = l.x / wv;
    float fp = 1.2 * th * gPxA / L / max(abs(dn), 0.15);
    float pu = max(fp / wv, 1e-5), pv = max(fp, 1e-5);
    float cover = smoothstep(0.04, 0.25, abs(dn)) * clamp((1.0 - abs(u)) / pu + 0.5, 0.0, 1.0) * clamp(v / pv + 0.5, 0.0, 1.0) * clamp((1.0 - v) / pv + 0.5, 0.0, 1.0);
    if (cover <= 0.0) return;
    float outline = clamp(1.4 - (1.0 - abs(u)) / pu, 0.0, 1.0);
    float rachis = clamp(1.2 - abs(u) / pu * 0.8, 0.0, 1.0) * (1.0 - smoothstep(0.9, 0.98, v));
    float bc = v * 16.0 + abs(u) * 2.2;
    float barb = clamp(1.0 - abs(fract(bc + 0.5) - 0.5) / (16.0 * pv + 2.2 * pu) + 0.3, 0.0, 1.0) * 0.8;
    float fres = pow(1.0 - abs(dn), 3.0);
    vec3 lineCol = mix(C_LINE, heatCol * 1.2, heat);
    vec3 edgeCol = mix(mix(C_RIM, C_HOT, 0.3 * v), heatCol * 1.5, heat);
    vec3 fill = mix(C_FILL, heatCol * 0.45, heat) * 0.5;
    vec3 lit = lineCol * barb * 0.75 + edgeCol * (outline * 1.15 + rachis * 0.9 + fres * 0.5);
    vec3 hw = gRo + gRd * th;
    float keep = smoothstep(SEA_Y - 0.01, SEA_Y + 0.03, hw.y) * clamp(hw.z / (th * gPxA) + 0.5, 0.0, 1.0);
    float a = 0.5 * alpha * keep * cover;
    vec3 col = (fill * 0.5 + lit) * alpha * keep * cover;
    gFeath = gFeath * (1.0 - a) + col;
    gFeathA = 1.0 - (1.0 - gFeathA) * (1.0 - a);
}

void feather(float k, float sg, vec3 Pa, vec3 Pb, vec4 Qa, float s, float L, float fanDeg, float td, float burn,
             float vx, float vy, float spin, vec3 SP, vec4 SQ, float ph, float flap) {
    float t = gT;
    float alpha = 1.0, heat;
    vec3 pos;
    vec4 q;
    if (t < td) {
        pos = mix(Pa, Pb, s);
        float fan = fanDeg * (1.0 + 0.10 * sin(ph - 0.9) * flap) * DEG;   // the fan opens on the downstroke
        q = qmul(qmul(Qa, qaa(vec3(0.0, 0.0, 1.0), atan(sg * cos(fan), sin(fan)))), qaa(vec3(0.0, 1.0, 0.0), sg * 0.25));
        if (t > td - 0.5) q = qmul(q, qaa(vec3(1.0, 0.0, 0.0), 0.25 * sin((t - td) * 40.0) * sm(td - 0.5, td, t)));   // a shiver before it lets go
        heat = sm(6.0, min(td, 9.5), t) * 0.75 * (td < 50.0 ? 1.0 : 0.5);
    } else {
        float tau = t - td;
        pos = SP + vec3(vx * tau + 0.12 * sin(tau * 2.3 + k), vy * tau - 0.32 * tau * tau / (1.0 + 0.45 * tau), 0.05 * sin(tau * 1.7 + k));
        q = qmul(qmul(SQ, qaa(vec3(0.0, 1.0, 0.0), spin * tau)), qaa(vec3(1.0, 0.0, 0.0), 0.6 * sin(tau * 3.1 + k)));
        if (burn < 50.0) { heat = min(1.0, 0.8 + tau / burn); alpha = 1.0 - sm(burn * 0.55, burn, tau); }
        else heat = max(0.15, 0.85 - tau * 0.25);   // cools as it drifts down
        if (t > 19.4) alpha *= 1.0 - sm(19.4, 19.9, t);
    }
    if (t < 0.4) alpha = 0.0;
    if (alpha < 0.002) return;
    vane(pos, q, L, heat, alpha);
}

// ---------------------------------------------------------------- wax and spray
void drop(vec3 pos, vec2 rad, vec3 c, float a) {
    float tz = (pos.z - gRo.z) / gRd.z;
    vec2 q = ((gRo + gRd * tz).xy - pos.xy) / rad;
    float r = length(q);
    float hz = tz * gPxA;
    float aa = 1.2 * hz / rad.x;
    float disc = 1.0 - smoothstep(1.0 - aa, 1.0 + aa, r);
    gDrops += mix(c * 1.6, c, smoothstep(0.0, 1.0, r)) * disc * a;
    float edge = max(r - 1.0, 0.0) * rad.x;
    gGlow += c * a * (0.14 * exp(-edge / (4.0 * hz)) + 0.02 * exp(-edge / (20.0 * hz)));
}
void wax(vec3 src, float t0, float vx) {
    float tau = gT - t0;
    if (tau <= 0.0 || tau >= 2.2) return;
    vec3 pos = src + vec3(vx * tau, -1.3 * tau * tau, 0.02);
    float a = sm(0.0, 0.1, tau) * (1.0 - sm(1.4, 2.2, tau)) * (pos.y > SEA_Y ? 1.0 : 0.0);
    float s = 0.016 * (1.0 + 0.6 * min(1.0, tau * 2.0));
    if (a > 0.002) drop(pos, 0.5 * vec2(s, s * (1.0 + min(0.8, tau * 0.8))), C_WAX, a);
}
void spray(float vx, float vy, float z, float delay, float size) {
    float tau = gT - IMPACT_T - delay;
    if (tau <= 0.0 || tau >= 1.6) return;
    vec3 pos = vec3(IMPACT_X + vx * tau, SEA_Y + vy * tau - 3.25 * tau * tau, 0.35 + z);
    float a = pos.y > SEA_Y - 0.01 ? 1.0 - sm(0.9, 1.5, tau) : 0.0;
    if (a > 0.002) drop(pos, vec2(0.5 * size), C_SPRAY, a);
}

// ---------------------------------------------------------------- wall, sun and sea
float seaRows(vec2 p) {
    float depth = 0.32 / (SEA_Y - p.y + 0.015);
    float X = p.x * depth;
    return log(depth) * 4.2 + 0.18 * sin(X * 1.3 + gT * 0.8) + 0.08 * sin(X * 3.4 - gT * 1.9);
}
float seaCols(vec2 p) { return p.x * 0.32 / (SEA_Y - p.y + 0.015) * 0.9; }
float rippleR(vec2 p, float ix) {
    float depth = 0.32 / (SEA_Y - p.y + 0.015);
    vec2 w = vec2(p.x * depth - ix * (0.32 / 0.24), depth - 0.32 / 0.24);
    return length(w * vec2(0.28, 1.0));
}

vec3 sea(vec2 p, float pw, out float lines) {
    vec3 col = vec3(0.0);
    float d = SEA_Y - p.y;
    col += C_HORIZON * (1.0 - smoothstep(0.0, 1.6 * pw, abs(d))) * 1.1;   // the horizon line
    lines = 0.0;
    if (d > 0.0) {
        vec2 dx = vec2(pw, 0.0), dy = vec2(0.0, -pw);
        float rows = seaRows(p);
        float fr = max(abs(seaRows(p + dx) - rows) + abs(seaRows(p + dy) - rows), 1e-5);
        float rowLine = clamp(1.25 - abs(fract(rows + 0.5) - 0.5) / fr, 0.0, 1.0) * (1.0 - smoothstep(0.12, 0.32, fr));
        float cols = seaCols(p);
        float fc = max(abs(seaCols(p + dx) - cols) + abs(seaCols(p + dy) - cols), 1e-5);
        float colLine = clamp(1.25 - abs(fract(cols + 0.5) - 0.5) / fc, 0.0, 1.0) * (1.0 - smoothstep(0.18, 0.55, fc)) * 0.45;
        // ripples from the splash, as rings on the water plane
        float since = gT - IMPACT_T;
        float strength = since > 0.0 ? 1.0 - sm(2.4, 4.0, since) : 0.0;
        float rip = 0.0, ripGlow = 0.0;
        if (strength > 0.0) {
            float r = rippleR(p, IMPACT_X);
            float fw = max(abs(rippleR(p + dx, IMPACT_X) - r) + abs(rippleR(p + dy, IMPACT_X) - r), 1e-5);
            for (int k = 0; k < 4; k++) {
                float R = since * 0.55 - float(k) * 0.22;
                if (R <= 0.0) continue;
                float ring = clamp(1.3 - abs(r - R) / fw, 0.0, 1.0);
                float fade = exp(-R * 0.6) * (1.0 - 0.18 * float(k)) * (1.0 - smoothstep(0.15, 0.5, fw));
                rip += ring * fade;
                ripGlow += exp(-abs(r - R) / (10.0 * fw)) * fade;
            }
            rip *= strength; ripGlow *= strength;
        }
        float fade = smoothstep(0.0, 0.25, d);
        lines = (rowLine + colLine) * (0.4 + 0.6 * fade);
        col += C_SEA_FILL * 0.6 * fade + C_SEA * lines * 0.75 + C_SPRAY * rip * 1.5 + (C_SEA + C_SPRAY) * ripGlow * 0.3;
    }
    return col;
}

vec3 sun(vec2 p, float pw) {
    vec2 vP = p - SUN.xy;
    float r = length(vP), a = atan(vP.y, vP.x);
    float disc = 1.0 - smoothstep(SUN_R - pw, SUN_R + pw, r);
    vec3 col = mix(C_SUN_CORE * 1.15, C_SUN_EDGE, smoothstep(0.0, SUN_R, r)) * disc;
    // rays: thin spokes whose length breathes and flares
    float s = a / TAU * 36.0 + gT * 0.12;
    float id = floor(s + 0.5);
    float len = SUN_R * (1.35 + 0.55 * hash1(id) * (0.7 + 0.3 * sin(gT * 1.3 + id))) * (0.85 + 0.35 * (gSunI - 1.0) + 0.15);
    float fa = max(1.2 * 36.0 / TAU * pw / max(r, 1e-4), 1e-5);
    float spoke = clamp(1.0 - abs(fract(s + 0.5) - 0.5) / fa * (r / SUN_R), 0.0, 1.0);
    col += C_SUN_EDGE * spoke * smoothstep(SUN_R * 1.02, SUN_R * 1.1, r) * (1.0 - smoothstep(len * 0.6, len, r)) * 0.9 * gSunI;
    col += C_SUN_EDGE * clamp(1.3 - abs(r - SUN_R * 1.18) / pw, 0.0, 1.0) * 0.6 * gSunI;   // a thin outer ring
    col += C_SUN_HALO * exp(-r * r / (SUN_R * SUN_R * 3.5)) * 0.45 * gSunI * (1.0 - disc);
    if (max(abs(vP.x), abs(vP.y)) > 0.9) col = vec3(0.0);   // the sun is a 1.8 x 1.8 card
    // bloom: the disc and its corona spread out over the sky
    float beyond = max(r - SUN_R, 0.0);
    col += C_SUN_CORE * disc * (1.1 - 0.6 * smoothstep(0.0, SUN_R, r)) + (C_SUN_EDGE * 0.5 + C_SUN_CORE * 0.25) * gSunI * (exp(-beyond / 0.04) * 0.6 + exp(-beyond / 0.3) * 0.25);
    return col * gSunI;
}

vec3 wall(vec2 p, vec4 view, float pw) {
    float vy = clamp((p.y - view.y) / (view.w - view.y), 0.0, 1.0);
    vec3 c = mix(C_WALL_BOT, C_WALL_TOP, smoothstep(0.0, 1.0, vy));
    float my = view.w - 0.075 * (view.w - view.y);   // a molding near the top
    float above = smoothstep(my - pw, my + pw, p.y);
    c *= mix(1.0, 1.07, above);
    float edgeL = 1.0 - smoothstep(0.0, 1.5 * pw, abs(p.y - my));
    float shadowLine = 1.0 - smoothstep(0.0, 1.5 * pw, abs(p.y - (my - 0.02)));
    c *= 1.0 - 0.05 * exp(-max(0.0, my - p.y) / 0.06) * (1.0 - above) - 0.10 * shadowLine;
    c += c * 0.10 * edgeL;
    vec2 cc = (p - 0.5 * (view.xy + view.zw)) / (0.5 * (view.zw - view.xy));
    c *= 1.0 - 0.10 * smoothstep(0.5, 1.5, length(cc));
    float ds = length(p - SUN.xy);
    vec3 light = C_SUN_HALO * gSunI * (0.55 * exp(-ds / 0.55) + 0.25 * exp(-ds / 1.6));
    return c + c * light * 2.0 + light * 0.12;
}

// ---------------------------------------------------------------- tone mapping (three.js ACES filmic, exposure 1.1)
vec3 aces(vec3 c) {
    c *= 1.1 / 0.6;
    c = mat3(0.59719, 0.07600, 0.02840, 0.35458, 0.90834, 0.13383, 0.04823, 0.01566, 0.83777) * c;
    vec3 a = c * (c + 0.0245786) - 0.000090537;
    vec3 b = c * (0.983729 * c + 0.4329510) + 0.238081;
    c = a / b;
    c = mat3(1.60475, -0.10208, -0.00327, -0.53108, 1.10813, -0.07276, -0.07367, -0.00605, 1.07602) * c;
    return clamp(c, 0.0, 1.0);
}
vec3 toSRGB(vec3 c) {
    return mix(pow(c, vec3(1.0 / 2.4)) * 1.055 - 0.055, c * 12.92, step(c, vec3(0.0031308)));
}

void main() {
    float t = mod(TIME * speed + timeOffset, LOOP);
    gT = t;
    gSunI = 1.0 + 0.55 * sm(6.0, 7.6, t) * (1.0 - sm(8.6, 10.5, t)) + 0.04 * sin(t * 7.3) * sin(t * 3.1);

    // camera: frames x -2.25..2.25, y -1.5..1.5 at the wall (z = 0), fov 30 degrees
    float aspect = RENDERSIZE.x / RENDERSIZE.y;
    float halfH = max(1.5, 2.25 / aspect), halfW = halfH * aspect;
    float D = halfH / 0.26794919;
    vec2 p = (isf_FragNormCoord * 2.0 - 1.0) * vec2(halfW, halfH);
    gRo = vec3(0.0, 0.0, D);
    gRd = normalize(vec3(p, 0.0) - gRo);
    float pw = 2.0 * halfH / RENDERSIZE.y;   // one pixel on the wall
    gPxA = pw / D;
    vec4 view = vec4(-halfW, -halfH, halfW, halfH);

    // the figure
    float ph;
    vec4 w;
    pose(t, vec3(-halfW - 1.9, -0.75, 0.3), ph, w);
    gFigAll = vec3(0.0); gFigBest = vec3(0.0); gBestT = 1e9; gTrans = 1.0;
    gFigBloom = vec3(0.0); gGlow = vec3(0.0);
    vec3 cA[18], cB[18], cU[18], cV[18];
    cA[0] = P_pelvis + qrot(Q_pelvis, vec3(-0.102, -0.017, 0.0)); cB[0] = P_pelvis + qrot(Q_pelvis, vec3(0.102, -0.017, 0.0));
    cU[0] = qrot(Q_pelvis, vec3(0.0, 0.0, 1.0)); cV[0] = qrot(Q_pelvis, vec3(0.0, -1.0, 0.0));
    cA[1] = P_spine + qrot(Q_spine, vec3(0.0, -0.085, 0.0)); cB[1] = P_spine + qrot(Q_spine, vec3(0.0, 0.0, 0.0));
    cU[1] = qrot(Q_spine, vec3(1.0, 0.0, 0.0)); cV[1] = qrot(Q_spine, vec3(0.0, 0.0, -1.0));
    cA[2] = P_chest + qrot(Q_chest, vec3(0.0, -0.187, 0.0)); cB[2] = P_chest + qrot(Q_chest, vec3(0.0, 0.051, 0.0));
    cU[2] = qrot(Q_chest, vec3(1.0, 0.0, 0.0)); cV[2] = qrot(Q_chest, vec3(0.0, 0.0, -1.0));
    cA[3] = P_chest + qrot(Q_chest, vec3(-0.1275, 0.119, 0.0)); cB[3] = P_chest + qrot(Q_chest, vec3(0.1275, 0.119, 0.0));
    cU[3] = qrot(Q_chest, vec3(0.0, 0.0, 1.0)); cV[3] = qrot(Q_chest, vec3(0.0, -1.0, 0.0));
    cA[4] = P_neck + qrot(Q_neck, vec3(0.0, -0.0255, 0.0)); cB[4] = P_neck + qrot(Q_neck, vec3(0.0, 0.0765, 0.0));
    cU[4] = qrot(Q_neck, vec3(0.0, 0.0, 1.0)); cV[4] = qrot(Q_neck, vec3(1.0, 0.0, 0.0));
    cA[5] = P_head + qrot(Q_head, vec3(0.0, 0.051, 0.0085)); cB[5] = P_head + qrot(Q_head, vec3(0.0, 0.1275, 0.0));
    cU[5] = qrot(Q_head, vec3(0.0, 0.110432, 0.993884)); cV[5] = qrot(Q_head, vec3(1.0, 0.0, 0.0));
    cA[6] = P_shoulder_L + qrot(Q_shoulder_L, vec3(0.0, 0.0, 0.0)); cB[6] = P_shoulder_L + qrot(Q_shoulder_L, vec3(0.255, 0.0, 0.0));
    cU[6] = qrot(Q_shoulder_L, vec3(0.0, 0.0, 1.0)); cV[6] = qrot(Q_shoulder_L, vec3(0.0, -1.0, 0.0));
    cA[7] = P_elbow_L + qrot(Q_elbow_L, vec3(0.0, 0.0, 0.0)); cB[7] = P_elbow_L + qrot(Q_elbow_L, vec3(0.221, 0.0, 0.0));
    cU[7] = qrot(Q_elbow_L, vec3(0.0, 0.0, 1.0)); cV[7] = qrot(Q_elbow_L, vec3(0.0, -1.0, 0.0));
    cA[8] = P_wrist_L + qrot(Q_wrist_L, vec3(0.0, 0.0, 0.0)); cB[8] = P_wrist_L + qrot(Q_wrist_L, vec3(0.1445, 0.0, 0.0));
    cU[8] = qrot(Q_wrist_L, vec3(0.0, 0.0, 1.0)); cV[8] = qrot(Q_wrist_L, vec3(0.0, -1.0, 0.0));
    cA[9] = P_hip_L + qrot(Q_hip_L, vec3(0.0, 0.0, 0.0)); cB[9] = P_hip_L + qrot(Q_hip_L, vec3(0.0, -0.374, 0.0));
    cU[9] = qrot(Q_hip_L, vec3(0.0, 0.0, 1.0)); cV[9] = qrot(Q_hip_L, vec3(-1.0, 0.0, 0.0));
    cA[10] = P_knee_L + qrot(Q_knee_L, vec3(0.0, 0.0, 0.0)); cB[10] = P_knee_L + qrot(Q_knee_L, vec3(0.0, -0.3655, 0.0));
    cU[10] = qrot(Q_knee_L, vec3(0.0, 0.0, 1.0)); cV[10] = qrot(Q_knee_L, vec3(-1.0, 0.0, 0.0));
    cA[11] = P_ankle_L + qrot(Q_ankle_L, vec3(0.0, 0.0, 0.0)); cB[11] = P_ankle_L + qrot(Q_ankle_L, vec3(0.0, -0.0425, 0.1275));
    cU[11] = qrot(Q_ankle_L, vec3(1.0, 0.0, 0.0)); cV[11] = qrot(Q_ankle_L, vec3(0.0, 0.948683, 0.316228));
    cA[12] = P_shoulder_R + qrot(Q_shoulder_R, vec3(0.0, 0.0, 0.0)); cB[12] = P_shoulder_R + qrot(Q_shoulder_R, vec3(-0.255, 0.0, 0.0));
    cU[12] = qrot(Q_shoulder_R, vec3(0.0, 0.0, 1.0)); cV[12] = qrot(Q_shoulder_R, vec3(0.0, 1.0, 0.0));
    cA[13] = P_elbow_R + qrot(Q_elbow_R, vec3(0.0, 0.0, 0.0)); cB[13] = P_elbow_R + qrot(Q_elbow_R, vec3(-0.221, 0.0, 0.0));
    cU[13] = qrot(Q_elbow_R, vec3(0.0, 0.0, 1.0)); cV[13] = qrot(Q_elbow_R, vec3(0.0, 1.0, 0.0));
    cA[14] = P_wrist_R + qrot(Q_wrist_R, vec3(0.0, 0.0, 0.0)); cB[14] = P_wrist_R + qrot(Q_wrist_R, vec3(-0.1445, 0.0, 0.0));
    cU[14] = qrot(Q_wrist_R, vec3(0.0, 0.0, 1.0)); cV[14] = qrot(Q_wrist_R, vec3(0.0, 1.0, 0.0));
    cA[15] = P_hip_R + qrot(Q_hip_R, vec3(0.0, 0.0, 0.0)); cB[15] = P_hip_R + qrot(Q_hip_R, vec3(0.0, -0.374, 0.0));
    cU[15] = qrot(Q_hip_R, vec3(0.0, 0.0, 1.0)); cV[15] = qrot(Q_hip_R, vec3(-1.0, 0.0, 0.0));
    cA[16] = P_knee_R + qrot(Q_knee_R, vec3(0.0, 0.0, 0.0)); cB[16] = P_knee_R + qrot(Q_knee_R, vec3(0.0, -0.3655, 0.0));
    cU[16] = qrot(Q_knee_R, vec3(0.0, 0.0, 1.0)); cV[16] = qrot(Q_knee_R, vec3(-1.0, 0.0, 0.0));
    cA[17] = P_ankle_R + qrot(Q_ankle_R, vec3(0.0, 0.0, 0.0)); cB[17] = P_ankle_R + qrot(Q_ankle_R, vec3(0.0, -0.0425, 0.1275));
    cU[17] = qrot(Q_ankle_R, vec3(1.0, 0.0, 0.0)); cV[17] = qrot(Q_ankle_R, vec3(0.0, 0.948683, 0.316228));
    for (int k = 0; k < 18; k++) {
        vec4 r; vec3 m;
        capData(k, r, m);
        capsule(cA[k], cB[k], cU[k], cV[k], r.x, r.y, r.z, r.w, m.x, m.y, m.z);
    }

    // feathers
    gFeath = vec3(0.0); gFeathA = 0.0;
    for (int k = 0; k < 24; k++) {
        vec4 a, b, SQ; vec3 SP;
        featherData(k, a, b, SP, SQ);
        int i = k < 12 ? k : k - 12;   // coverts 0-2 on the upper arm, secondaries 3-6, primaries 7-11
        vec3 Pa, Pb; vec4 Qa;
        if (k < 12) {
            if (i < 3) { Pa = P_shoulder_L; Pb = P_elbow_L; Qa = Q_shoulder_L; }
            else if (i < 7) { Pa = P_elbow_L; Pb = P_wrist_L; Qa = Q_elbow_L; }
            else { Pa = P_wrist_L; Pb = P_hand_L; Qa = Q_wrist_L; }
        } else {
            if (i < 3) { Pa = P_shoulder_R; Pb = P_elbow_R; Qa = Q_shoulder_R; }
            else if (i < 7) { Pa = P_elbow_R; Pb = P_wrist_R; Qa = Q_elbow_R; }
            else { Pa = P_wrist_R; Pb = P_hand_R; Qa = Q_wrist_R; }
        }
        feather(float(k), k < 12 ? 1.0 : -1.0, Pa, Pb, Qa, a.x, a.y, a.z, a.w, b.x, b.y, b.z, b.w, SP, SQ, ph, w.x);
    }

    // wax drips and spray
    gDrops = vec3(0.0);
    if (t > 6.775003 && t < 11.141987) {
        for (int k = 0; k < 18; k++) { vec3 src; vec2 d; waxData(k, src, d); wax(src, d.x, d.y); }
    }
    if (t > IMPACT_T && t < 15.647557) {
        for (int k = 0; k < 46; k++) { vec4 a; float size; sprayData(k, a, size); spray(a.x, a.y, a.z, a.w, size); }
    }

    // background: the sun card and the projected sea (on the sage wall unless projector mode)
    float seaLines;
    vec3 seaCol = sea(p, pw, seaLines);
    float ds = length(p - SUN.xy);
    vec3 bg = projector ? C_SUN_HALO * gSunI * 0.3 * exp(-ds / 0.45) + seaCol : wall(p, view, pw) + seaCol;
    bg += sun(p, pw);
    // the horizon glows a little
    float dh = abs(SEA_Y - p.y);
    bg += C_HORIZON * (0.14 * exp(-dh / 0.02) + 0.06 * exp(-dh / 0.12));

    vec3 fig = gFigBest + 0.45 * (gFigAll - gFigBest);
    vec3 col = bg * gTrans + fig;
    col = col * (1.0 - gFeathA) + gFeath;
    col += gDrops;
    col += (gGlow * 0.6 + gFigBloom * 0.3) * glow;

    gl_FragColor = vec4(toSRGB(aces(col)) * intensity, 1.0);
}
