/*{
    "DESCRIPTION": "Holo Arm and Lamp: a holographic wireframe arm reaches onto the wall, keystones a calibration grid, carries a glowing orb into a wall lamp, pulls the flame back out and relights it, then switches the lamp red, green and warm white with a finger gun. A 20-second seamless loop, ray traced live in the shader (scenes/holo_arm_lamp.html with its procedural hand).",
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

// Holo Arm and Lamp as an ISF generator, ported from scenes/holo_arm_lamp.html
// (the procedural hand: a shader can't load the scanned hand model). The
// numbers below (colors, the hand rig, poses, the camera-fit tracks for a
// 16:9 frame, the orb's spring-follow path, the bolt origins) are baked from
// that scene's own math.
//
// Every pixel evaluates the arm for the current time (tracks, IK, finger
// forward kinematics), then ray traces the hand (19 capsules), the forearm,
// upper arm and elbow, each with the 1-pixel lattice and fresnel rim, front
// and back faces. The wall, lamp, calibration grid, orb and bolts are drawn
// analytically, and bloom is approximated with halos. World units are
// meters on the wall; the frame always fits x -0.55..0.65, y -0.42..0.42.

#define LOOP 20.0
#define PI 3.14159265
#define TAU 6.28318531
#define DEG 0.01745329
#define HS 2.2
#define CELL 0.00625
#define GLOW_NEAR 0.18
#define GLOW_FAR 0.07

const vec3 C_WALL_TOP = vec3(0.1535952, 0.1903166, 0.1490598);
const vec3 C_WALL_BOT = vec3(0.1018006, 0.1280064, 0.1034589);
const vec3 C_WEAVE = vec3(0.4859378, 0.3183594, 0.1659636);
const vec3 C_FILL = vec3(0.0044208, 0.146538, 0.6955594);
const vec3 C_LINE = vec3(0.0, 0.4062223, 1.5583027);
const vec3 C_RIM = vec3(0.0, 0.9626815, 8.139224);
const vec3 C_HOT = vec3(0.0, 4.0225146, 13.2040391);
const vec3 C_ORB_CORE = vec3(0.0, 8.1456419, 0.0);
const vec3 C_ORB_EDGE = vec3(0.1037801, 1.502748, 0.0);
const vec3 C_MINT = vec3(0.0, 5.4559552, 1.216138);
const vec3 C_MINT_FILL = vec3(0.0370209, 0.0977684, 0.0893387);
const vec3 LAMP_POS = vec3(-0.3, -0.2, 0.035);
const vec2 SHADE_HALF = vec2(0.085, 0.065);
const vec3 RIG_W = vec3(0.03913, 0.05578, 0.00916);
const vec3 RIG_F = vec3(-0.0278075, -0.9994353, -0.0188655);
const vec3 RIG_L = vec3(-0.1160442, -0.0155176, 0.9931228);
const vec3 RIG_N = vec3(-0.9928547, 0.0298055, -0.1155472);
const vec4 REST_BASIS_INV = vec4(0.4671766, -0.5396099, 0.5165688, -0.4729947);
const vec3 KN0 = vec3(0.03181, -0.03277, -0.01439);
const vec3 KN1 = vec3(0.03658, -0.03587, 0.00743);
const vec3 KN2 = vec3(0.0326, -0.02892, 0.02662);
const vec3 KN3 = vec3(0.02543, -0.01812, 0.04421);

// ---------------------------------------------------------------- per-pixel state
vec3 gRo, gRd;          // camera ray
float gPxA;             // world size of one pixel per unit of distance
float gT;
vec3 gW;                // wrist (world)
vec4 gQroot;            // hand armature -> world rotation
vec3 gLampCol;          // the lamp's light on the fingertips
vec3 gOrbPos, gOrbCol;
vec3 gFigAll, gFigBest; float gBestT, gTrans;
vec3 gFigBloom, gGlow;

// ---------------------------------------------------------------- math
float sm(float a, float b, float x) { float u = clamp((x - a) / (b - a), 0.0, 1.0); return u * u * (3.0 - 2.0 * u); }
float easeIO(float u) { return u < 0.5 ? 4.0 * u * u * u : 1.0 - pow(-2.0 * u + 2.0, 3.0) / 2.0; }
float easeInCubic(float u) { return u * u * u; }
float settleBump(float u) { return sin(PI * clamp((u - 0.62) / 0.38, 0.0, 1.0)); }
float settleBase(float u) { return easeIO(clamp(u / 0.8, 0.0, 1.0)); }
float hash1(float n) { return fract(sin(n) * 43758.5453); }
float noise1(float x) { float i = floor(x), fr = fract(x); return mix(hash1(i), hash1(i + 1.0), fr * fr * (3.0 - 2.0 * fr)); }

vec4 qmul(vec4 a, vec4 b) {
    return vec4(a.x * b.w + a.w * b.x + a.y * b.z - a.z * b.y,
                a.y * b.w + a.w * b.y + a.z * b.x - a.x * b.z,
                a.z * b.w + a.w * b.z + a.x * b.y - a.y * b.x,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
}
vec3 qrot(vec4 q, vec3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }
vec4 qconj(vec4 q) { return vec4(-q.xyz, q.w); }
vec4 qaa(vec3 axis, float a) { return vec4(axis * sin(a * 0.5), cos(a * 0.5)); }
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
// Catmull-Rom through a, b, c (the scene's lerpV path)
vec3 catmull3(vec3 a, vec3 b, vec3 c, float k) {
    float x = k * 2.0;
    if (x < 1.0) return crSeg(a, a, b, c, x);
    return crSeg(a, b, c, c, x - 1.0);
}

// ---------------------------------------------------------------- tracks
vec3 shoulderTrack(float t) {
    vec3 v = vec3(1.6966667, -0.05, 0.42);
    if (t >= 2.6) { float u = clamp((t - 2.6) / 1.2, 0.0, 1.0); v = u >= 1.0 ? vec3(0.55, 1.17, 0.48) : mix(v, vec3(0.55, 1.17, 0.48), easeIO(u)); }
    if (t >= 10.2) { float u = clamp((t - 10.2) / 0.8, 0.0, 1.0); v = u >= 1.0 ? vec3(1.1466667, 0.97, 0.45) : mix(v, vec3(1.1466667, 0.97, 0.45), easeIO(u)); }
    if (t >= 11.0) { float u = clamp((t - 11.0) / 0.8, 0.0, 1.0); v = u >= 1.0 ? vec3(0.7, 1.17, 0.48) : mix(v, vec3(0.7, 1.17, 0.48), easeIO(u)); }
    if (t >= 12.3) { float u = clamp((t - 12.3) / 0.8, 0.0, 1.0); v = u >= 1.0 ? vec3(1.1466667, 0.97, 0.45) : mix(v, vec3(1.1466667, 0.97, 0.45), easeIO(u)); }
    if (t >= 13.1) { float u = clamp((t - 13.1) / 0.2, 0.0, 1.0); v = u >= 1.0 ? vec3(1.6966667, -0.05, 0.42) : mix(v, vec3(1.6966667, -0.05, 0.42), easeIO(u)); }
    return v;
}
vec3 poleTrack(float t) {
    vec3 v = vec3(1.0, -0.6, 0.6);
    if (t >= 2.6) { float u = clamp((t - 2.6) / 1.2, 0.0, 1.0); v = u >= 1.0 ? vec3(1.2, 0.4, 1.0) : mix(v, vec3(1.2, 0.4, 1.0), easeIO(u)); }
    if (t >= 10.2) { float u = clamp((t - 10.2) / 1.0, 0.0, 1.0); v = u >= 1.0 ? vec3(1.3, 0.8, 1.0) : mix(v, vec3(1.3, 0.8, 1.0), easeIO(u)); }
    if (t >= 12.6) { float u = clamp((t - 12.6) / 0.6, 0.0, 1.0); v = u >= 1.0 ? vec3(0.6, -1.0, 0.8) : mix(v, vec3(0.6, -1.0, 0.8), easeIO(u)); }
    return v;
}
vec3 wristTrack(float t) {
    vec3 v = vec3(1.4166667, -0.1, 0.3);
    if (t >= 2.6) { float u = clamp((t - 2.6) / 1.2, 0.0, 1.0); v = u >= 1.0 ? vec3(0.12, 1.04, 0.3) : catmull3(v, vec3(1.5466667, 1.12, 0.3), vec3(0.12, 1.04, 0.3), easeIO(u)); }
    if (t >= 4.75) { float u = clamp((t - 4.75) / 1.3, 0.0, 1.0); v = u >= 1.0 ? vec3(-0.13, 0.155, 0.3) : mix(v, vec3(-0.13, 0.155, 0.3), easeIO(u)); }
    if (t >= 7.3) { float u = clamp((t - 7.3) / 0.6, 0.0, 1.0); v = u >= 1.0 ? vec3(-0.1, 0.215, 0.3) : mix(v, vec3(-0.1, 0.215, 0.3), easeIO(u)); }
    if (t >= 8.0) { float u = clamp((t - 8.0) / 0.85, 0.0, 1.0); v = u >= 1.0 ? vec3(-0.165, -0.015, 0.26) : mix(v, vec3(-0.165, -0.015, 0.26), easeIO(u)); }
    if (t >= 9.35) { float u = clamp((t - 9.35) / 1.1, 0.0, 1.0); v = u >= 1.0 ? vec3(0.17, 0.3, 0.3) : mix(v, vec3(0.17, 0.3, 0.3), easeIO(u)); }
    if (t >= 10.45) { float u = clamp((t - 10.45) / 0.6, 0.0, 1.0); v = u >= 1.0 ? vec3(0.58, 0.72, 0.3) : mix(v, vec3(0.58, 0.72, 0.3), easeIO(u)); }
    if (t >= 11.05) { float u = clamp((t - 11.05) / 0.85, 0.0, 1.0); v = u >= 1.0 ? vec3(0.03, 0.21, 0.3) : catmull3(v, vec3(0.4, 0.42, 0.3), vec3(0.03, 0.21, 0.3), easeIO(u)); }
    if (t >= 11.9) { float u = clamp((t - 11.9) / 0.22, 0.0, 1.0); v = u >= 1.0 ? vec3(-0.03, 0.15, 0.3) : mix(v, vec3(-0.03, 0.15, 0.3), easeIO(u)); }
    if (t >= 12.3) { float u = clamp((t - 12.3) / 0.8, 0.0, 1.0); v = u >= 1.0 ? vec3(1.5466667, 1.12, 0.3) : catmull3(v, vec3(0.45, 0.92, 0.3), vec3(1.5466667, 1.12, 0.3), easeIO(u)); }
    if (t >= 13.1) { float u = clamp((t - 13.1) / 0.2, 0.0, 1.0); v = u >= 1.0 ? vec3(1.4166667, -0.1, 0.3) : mix(v, vec3(1.4166667, -0.1, 0.3), easeIO(u)); }
    if (t >= 13.3) { float u = clamp((t - 13.3) / 0.75, 0.0, 1.0); v = u >= 1.0 ? vec3(0.47, -0.105, 0.3) : mix(v, vec3(0.47, -0.105, 0.3), easeIO(u)); }
    if (t >= 19.0) { float u = clamp((t - 19.0) / 0.75, 0.0, 1.0); v = u >= 1.0 ? vec3(1.4166667, -0.1, 0.3) : mix(v, vec3(1.4166667, -0.1, 0.3), easeInCubic(u)); }
    return v;
}
vec4 handTrack(float t) {
    vec4 v = vec4(0.5679786, -0.4211892, -0.5462368, 0.4490272);
    if (t >= 2.6) { float u = clamp((t - 2.6) / 1.2, 0.0, 1.0); v = u >= 1.0 ? vec4(-0.6831582, -0.0690554, 0.7111834, -0.1508122) : qslerp(v, vec4(-0.6831582, -0.0690554, 0.7111834, -0.1508122), easeIO(u)); }
    if (t >= 6.3) { float u = clamp((t - 6.3) / 0.7, 0.0, 1.0); v = u >= 1.0 ? vec4(-0.6805399, -0.0977173, 0.6978373, -0.200848) : qslerp(v, vec4(-0.6805399, -0.0977173, 0.6978373, -0.200848), easeIO(u)); }
    if (t >= 7.3) { float u = clamp((t - 7.3) / 0.7, 0.0, 1.0); v = u >= 1.0 ? vec4(-0.6831582, -0.0690554, 0.7111834, -0.1508122) : qslerp(v, vec4(-0.6831582, -0.0690554, 0.7111834, -0.1508122), easeIO(u)); }
    if (t >= 8.0) { float u = clamp((t - 8.0) / 0.85, 0.0, 1.0); v = u >= 1.0 ? vec4(-0.6479613, -0.1339493, 0.7264095, -0.1858305) : qslerp(v, vec4(-0.6479613, -0.1339493, 0.7264095, -0.1858305), easeIO(u)); }
    if (t >= 9.35) { float u = clamp((t - 9.35) / 0.6, 0.0, 1.0); v = u >= 1.0 ? vec4(-0.5777861, -0.3173937, 0.6669891, -0.3472032) : qslerp(v, vec4(-0.5777861, -0.3173937, 0.6669891, -0.3472032), easeIO(u)); }
    if (t >= 10.0) { float u = clamp((t - 10.0) / 0.8, 0.0, 1.0); v = u >= 1.0 ? vec4(-0.6567989, -0.0957484, 0.7324243, -0.1516642) : qslerp(v, vec4(-0.6567989, -0.0957484, 0.7324243, -0.1516642), easeIO(u)); }
    if (t >= 11.05) { float u = clamp((t - 11.05) / 0.8, 0.0, 1.0); v = u >= 1.0 ? vec4(0.6568048, 0.1329753, -0.6357914, 0.383007) : qslerp(v, vec4(0.6568048, 0.1329753, -0.6357914, 0.383007), easeIO(u)); }
    if (t >= 11.85) { float u = clamp((t - 11.85) / 0.27, 0.0, 1.0); v = u >= 1.0 ? vec4(0.4861985, 0.3424093, -0.5519683, 0.5845493) : qslerp(v, vec4(0.4861985, 0.3424093, -0.5519683, 0.5845493), easeIO(u)); }
    if (t >= 12.4) { float u = clamp((t - 12.4) / 0.7, 0.0, 1.0); v = u >= 1.0 ? vec4(0.5679786, -0.4211892, -0.5462368, 0.4490272) : qslerp(v, vec4(0.5679786, -0.4211892, -0.5462368, 0.4490272), easeIO(u)); }
    if (t >= 13.3) { float u = clamp((t - 13.3) / 0.75, 0.0, 1.0); v = u >= 1.0 ? vec4(0.5213512, -0.4672027, -0.4659203, 0.5411403) : qslerp(v, vec4(0.5213512, -0.4672027, -0.4659203, 0.5411403), easeIO(u)); }
    if (t >= 18.6) { float u = clamp((t - 18.6) / 0.6, 0.0, 1.0); v = u >= 1.0 ? vec4(0.6000799, -0.3740375, -0.5056984, 0.4942359) : qslerp(v, vec4(0.6000799, -0.3740375, -0.5056984, 0.4942359), easeIO(u)); }
    return v;
}

void fingerKey(int k, out vec2 kt, out int pose) {
    if (k < 5) {
        if (k < 2) {
            if (k < 1) {
                kt = vec2(0.0, 0.0); pose = 1;
            } else {
                kt = vec2(3.0, 3.6); pose = 2;
            }
        } else {
            if (k < 3) {
                kt = vec2(6.35, 6.95); pose = 0;
            } else {
                if (k < 4) {
                    kt = vec2(7.4, 8.0); pose = 1;
                } else {
                    kt = vec2(8.5, 9.15); pose = 3;
                }
            }
        }
    } else {
        if (k < 7) {
            if (k < 6) {
                kt = vec2(11.93, 12.13); pose = 0;
            } else {
                kt = vec2(12.4, 12.95); pose = 1;
            }
        } else {
            if (k < 8) {
                kt = vec2(13.45, 14.0); pose = 4;
            } else {
                if (k < 9) {
                    kt = vec2(18.6, 19.15); pose = 0;
                } else {
                    kt = vec2(19.4, 20.0); pose = 1;
                }
            }
        }
    }
}
void poseTable(int k, out vec4 a) {
    if (k < 12) {
        if (k < 6) {
            if (k < 3) {
                if (k < 1) {
                    a = vec4(5.0, 2.0, 4.0, 4.0);
                } else {
                    if (k < 2) {
                        a = vec4(3.0, 5.0, 3.0, -5.0);
                    } else {
                        a = vec4(3.0, 5.0, 3.0, 0.0);
                    }
                }
            } else {
                if (k < 4) {
                    a = vec4(4.0, 6.0, 4.0, 4.0);
                } else {
                    if (k < 5) {
                        a = vec4(5.0, 6.0, 4.0, 9.0);
                    } else {
                        a = vec4(18.0, 8.0, 14.0, 10.0);
                    }
                }
            }
        } else {
            if (k < 9) {
                if (k < 7) {
                    a = vec4(16.0, 22.0, 14.0, -2.0);
                } else {
                    if (k < 8) {
                        a = vec4(20.0, 28.0, 17.0, 0.0);
                    } else {
                        a = vec4(24.0, 32.0, 19.0, 2.0);
                    }
                }
            } else {
                if (k < 10) {
                    a = vec4(28.0, 34.0, 21.0, 5.0);
                } else {
                    if (k < 11) {
                        a = vec4(38.0, 14.0, 22.0, 18.0);
                    } else {
                        a = vec4(38.0, 42.0, 28.0, -3.0);
                    }
                }
            }
        }
    } else {
        if (k < 18) {
            if (k < 15) {
                if (k < 13) {
                    a = vec4(42.0, 48.0, 30.0, 0.0);
                } else {
                    if (k < 14) {
                        a = vec4(45.0, 50.0, 32.0, 3.0);
                    } else {
                        a = vec4(48.0, 52.0, 34.0, 6.0);
                    }
                }
            } else {
                if (k < 16) {
                    a = vec4(52.0, 18.0, 30.0, 32.0);
                } else {
                    if (k < 17) {
                        a = vec4(58.0, 72.0, 48.0, -1.0);
                    } else {
                        a = vec4(62.0, 78.0, 52.0, 0.0);
                    }
                }
            }
        } else {
            if (k < 21) {
                if (k < 19) {
                    a = vec4(66.0, 80.0, 54.0, 2.0);
                } else {
                    if (k < 20) {
                        a = vec4(68.0, 82.0, 56.0, 4.0);
                    } else {
                        a = vec4(-4.0, -6.0, 0.0, 2.0);
                    }
                }
            } else {
                if (k < 23) {
                    if (k < 22) {
                        a = vec4(0.0, 3.0, 2.0, -2.0);
                    } else {
                        a = vec4(84.0, 100.0, 64.0, 0.0);
                    }
                } else {
                    if (k < 24) {
                        a = vec4(88.0, 102.0, 66.0, 2.0);
                    } else {
                        a = vec4(90.0, 104.0, 66.0, 4.0);
                    }
                }
            }
        }
    }
}
vec4 poseOf(int pose, int finger) { vec4 a; poseTable(pose * 5 + finger, a); return a; }
// [mcp, pip, dip, spread] (thumb: [oppose, cmc, mcp, ip]) with stagger, a settle overshoot and idle flutter
vec4 fingerAng(int finger, float tf, float t) {
    vec2 kt; int pose;
    fingerKey(0, kt, pose);
    vec4 prev = poseOf(pose, finger), o = prev;
    for (int i = 1; i < 10; i++) {
        fingerKey(i, kt, pose);
        if (tf < kt.x) break;
        vec4 target = poseOf(pose, finger);
        float u = clamp((tf - kt.x) / (kt.y - kt.x), 0.0, 1.0);
        if (u >= 1.0) { prev = target; o = prev; continue; }
        vec4 d = target - prev;
        o = prev + d * settleBase(u) + sign(d) * min(vec4(4.0), abs(d) * 0.18) * settleBump(u);
        break;
    }
    float ph = float(finger) * 1.7, w = t * TAU / 1.9 + ph;
    return o + vec4(1.1 * sin(w), 1.1 * sin(w + 1.0), 1.1 * sin(w + 2.0), 0.0);
}

vec3 jp_wrist; vec4 jq_wrist;
vec3 jp_thumb_metacarpal; vec4 jq_thumb_metacarpal;
vec3 jp_thumb_proximal; vec4 jq_thumb_proximal;
vec3 jp_thumb_distal; vec4 jq_thumb_distal;
vec3 jp_thumb_tip; vec4 jq_thumb_tip;
vec3 jp_index_metacarpal; vec4 jq_index_metacarpal;
vec3 jp_index_proximal; vec4 jq_index_proximal;
vec3 jp_index_intermediate; vec4 jq_index_intermediate;
vec3 jp_index_distal; vec4 jq_index_distal;
vec3 jp_index_tip; vec4 jq_index_tip;
vec3 jp_middle_metacarpal; vec4 jq_middle_metacarpal;
vec3 jp_middle_proximal; vec4 jq_middle_proximal;
vec3 jp_middle_intermediate; vec4 jq_middle_intermediate;
vec3 jp_middle_distal; vec4 jq_middle_distal;
vec3 jp_middle_tip; vec4 jq_middle_tip;
vec3 jp_ring_metacarpal; vec4 jq_ring_metacarpal;
vec3 jp_ring_proximal; vec4 jq_ring_proximal;
vec3 jp_ring_intermediate; vec4 jq_ring_intermediate;
vec3 jp_ring_distal; vec4 jq_ring_distal;
vec3 jp_ring_tip; vec4 jq_ring_tip;
vec3 jp_pinky_metacarpal; vec4 jq_pinky_metacarpal;
vec3 jp_pinky_proximal; vec4 jq_pinky_proximal;
vec3 jp_pinky_intermediate; vec4 jq_pinky_intermediate;
vec3 jp_pinky_distal; vec4 jq_pinky_distal;
vec3 jp_pinky_tip; vec4 jq_pinky_tip;

vec3 toWorld(vec3 x) { return gW + qrot(gQroot, HS * (x - RIG_W)); }

// shoulder, elbow and wrist (world), hand orientation; then the fingers
void evalArm(float t, out vec3 Sv, out vec3 E, out vec3 W, out vec4 Qk) {
    vec3 S = shoulderTrack(t);
    vec3 pole = poleTrack(t - 0.04);
    W = wristTrack(t - 0.08);
    Qk = handTrack(t - 0.08);
    // breathing drift
    float br = TAU / 3.1;
    W.x += sin(t * br) * 0.0015; W.y += sin(t * br + 1.3) * 0.0012;
    vec3 Nk = qrot(Qk, vec3(0.0, 1.0, 0.0)), Lk = qrot(Qk, vec3(1.0, 0.0, 0.0));
    Qk = qmul(qaa(Lk, 1.4 * DEG * sin(t * br + 0.4)), Qk);
    Qk = qmul(qaa(Nk, 1.0 * DEG * sin(t * br * 0.8 + 2.0)), Qk);
    // recoil after each shot: the wrist kicks up and springs back
    {
        float tau = t - 14.4;
        if (tau >= 0.0 && tau < 0.6) {
            float k = (tau / 0.05) * exp(1.0 - tau / 0.05);
            vec3 F = qrot(Qk, vec3(0.0, 0.0, 1.0));
            Qk = qmul(qaa(normalize(cross(F, vec3(0.0, 1.0, 0.0))), 12.0 * DEG * k), Qk);
            W -= F * 0.012 * k;
        }
    }
    {
        float tau = t - 15.9;
        if (tau >= 0.0 && tau < 0.6) {
            float k = (tau / 0.05) * exp(1.0 - tau / 0.05);
            vec3 F = qrot(Qk, vec3(0.0, 0.0, 1.0));
            Qk = qmul(qaa(normalize(cross(F, vec3(0.0, 1.0, 0.0))), 12.0 * DEG * k), Qk);
            W -= F * 0.012 * k;
        }
    }
    {
        float tau = t - 17.4;
        if (tau >= 0.0 && tau < 0.6) {
            float k = (tau / 0.05) * exp(1.0 - tau / 0.05);
            vec3 F = qrot(Qk, vec3(0.0, 0.0, 1.0));
            Qk = qmul(qaa(normalize(cross(F, vec3(0.0, 1.0, 0.0))), 12.0 * DEG * k), Qk);
            W -= F * 0.012 * k;
        }
    }
    // IK: a virtual shoulder within reach on the line toward the real one
    float a = 0.66, b = 0.572;
    vec3 toS = S - W;
    float dS = length(toS);
    Sv = W + normalize(toS) * min(dS, (a + b) * 0.96);
    vec3 d = W - Sv;
    float len = length(d);
    d /= len;
    float along = (a * a + len * len - b * b) / (2.0 * len);
    float hgt = sqrt(max(0.0, a * a - along * along));
    vec3 pv = normalize(pole - d * dot(pole, d));
    E = Sv + d * along + pv * hgt;
    // keep the wrist anatomical: limit the bend between forearm and hand
    vec3 fore = normalize(W - E);
    vec3 F = qrot(Qk, vec3(0.0, 0.0, 1.0));
    float bend = acos(clamp(dot(F, fore), -1.0, 1.0));
    if (bend > 62.0 * DEG) Qk = qmul(qaa(normalize(cross(F, fore)), bend - 62.0 * DEG), Qk);

    gW = W;
    gQroot = qmul(Qk, REST_BASIS_INV);
    vec4 fa;
    jp_wrist = vec3(0.03913, 0.05578, 0.00916); jq_wrist = vec4(-0.5, -0.5, -0.5, 0.5);
    fa = fingerAng(0, t - 0.07, t);
    jp_thumb_metacarpal = jp_wrist + qrot(jq_wrist, vec3(-0.02803, -0.01916, -0.03596)); jq_thumb_metacarpal = qmul(jq_wrist, vec4(0.00696, 0.3745905, 0.6295508, 0.6806609));
    jq_thumb_metacarpal = qmul(qmul(jq_thumb_metacarpal, qaa(vec3(0.4782981, -0.4705435, -0.7414983), fa.x * DEG)), qaa(vec3(-0.790326, 0.6123522, 0.0202367), fa.y * DEG));
    jp_thumb_proximal = jp_thumb_metacarpal + qrot(jq_thumb_metacarpal, vec3(-0.0001645, 0.0000906, -0.0325137)); jq_thumb_proximal = qmul(jq_thumb_metacarpal, vec4(0.0836743, -0.0767804, -0.0840286, 0.989971));
    jq_thumb_proximal = qmul(jq_thumb_proximal, qaa(vec3(-0.8768015, 0.4774109, 0.0574273), fa.z * DEG));
    jp_thumb_distal = jp_thumb_proximal + qrot(jq_thumb_proximal, vec3(-0.000148, 0.0000637, -0.0338002)); jq_thumb_distal = qmul(jq_thumb_proximal, vec4(0.0591683, 0.0616759, 0.0842761, 0.9927702));
    jq_thumb_distal = qmul(jq_thumb_distal, qaa(vec3(-0.7807646, 0.6149493, -0.1106528), fa.w * DEG));
    jp_thumb_tip = jp_thumb_distal + qrot(jq_thumb_distal, vec3(-0.0001522, 0.0000297, -0.0148553)); jq_thumb_tip = qmul(jq_thumb_distal, vec4(0.0003558, 0.0075593, -0.0039738, 0.9999635));
    fa = fingerAng(1, t - 0.08, t);
    jp_index_metacarpal = jp_wrist + qrot(jq_wrist, vec3(-0.01017, -0.00707, -0.02922)); jq_index_metacarpal = qmul(jq_wrist, vec4(-0.00436, 0.06273, 0.02099, 0.9978003));
    jp_index_proximal = jp_index_metacarpal + qrot(jq_index_metacarpal, vec3(-0.0058352, 0.000678, -0.0605362)); jq_index_proximal = qmul(jq_index_metacarpal, vec4(-0.0448548, -0.0696827, 0.0077071, 0.9965305));
    jq_index_proximal = qmul(jq_index_proximal, qaa(vec3(-0.9831848, 0.1804909, -0.0277627), fa.x * DEG));
    jq_index_proximal = qmul(jq_index_proximal, qaa(vec3(-0.1785523, -0.9820291, -0.061139), fa.w * DEG));
    jp_index_intermediate = jp_index_proximal + qrot(jq_index_proximal, vec3(0.0007396, 0.0009745, -0.0453345)); jq_index_intermediate = qmul(jq_index_proximal, vec4(0.000083, -0.0164042, -0.0285189, 0.9994586));
    jq_index_intermediate = qmul(jq_index_intermediate, qaa(vec3(-0.9922563, 0.124121, 0.004635), fa.y * DEG));
    jp_index_distal = jp_index_intermediate + qrot(jq_index_intermediate, vec3(0.000077, 0.0000211, -0.0242981)); jq_index_distal = qmul(jq_index_intermediate, vec4(0.0733251, -0.0257357, -0.0142441, 0.9968742));
    jq_index_distal = qmul(jq_index_distal, qaa(vec3(-0.9943045, 0.0989826, 0.0395106), fa.z * DEG));
    jp_index_tip = jp_index_distal + qrot(jq_index_distal, vec3(0.0000619, -0.0000173, -0.0115847)); jq_index_tip = qmul(jq_index_distal, vec4(-0.0003929, -0.0109922, -0.0006286, 0.9999393));
    fa = fingerAng(2, t - 0.112, t);
    jp_middle_metacarpal = jp_wrist + qrot(jq_wrist, vec3(-0.00343, -0.00606, -0.02911)); jq_middle_metacarpal = qmul(jq_wrist, vec4(-0.005945, 0.0221949, -0.0573248, 0.9980911));
    jp_middle_proximal = jp_middle_metacarpal + qrot(jq_middle_metacarpal, vec3(0.0040128, 0.0045821, -0.0623648)); jq_middle_proximal = qmul(jq_middle_metacarpal, vec4(-0.0439766, -0.0744117, 0.0463834, 0.9951771));
    jq_middle_proximal = qmul(jq_middle_proximal, qaa(vec3(-0.9950741, 0.0990976, -0.0026879), fa.x * DEG));
    jq_middle_proximal = qmul(jq_middle_proximal, qaa(vec3(-0.0987126, -0.9929773, -0.0652059), fa.w * DEG));
    jp_middle_intermediate = jp_middle_proximal + qrot(jq_middle_proximal, vec3(-0.0002157, 0.0005242, -0.0468861)); jq_middle_intermediate = qmul(jq_middle_proximal, vec4(0.005204, -0.0201968, -0.0104087, 0.9997283));
    jq_middle_intermediate = qmul(jq_middle_intermediate, qaa(vec3(-0.9962382, 0.0785415, 0.0366164), fa.y * DEG));
    jp_middle_distal = jp_middle_intermediate + qrot(jq_middle_intermediate, vec3(-0.000762, -0.0001154, -0.0275379)); jq_middle_distal = qmul(jq_middle_intermediate, vec4(0.0923191, 0.0058554, -0.0352628, 0.9950877));
    jq_middle_distal = qmul(jq_middle_distal, qaa(vec3(-0.9997844, 0.0127276, 0.0164037), fa.z * DEG));
    jp_middle_tip = jp_middle_distal + qrot(jq_middle_distal, vec3(-0.0000664, -0.0000479, -0.0122017)); jq_middle_tip = qmul(jq_middle_distal, vec4(-0.0012971, 0.0061336, -0.0025653, 0.9999771));
    fa = fingerAng(3, t - 0.144, t);
    jp_ring_metacarpal = jp_wrist + qrot(jq_wrist, vec3(0.00809, -0.00606, -0.027)); jq_ring_metacarpal = qmul(jq_wrist, vec4(-0.01173, -0.0664902, -0.1264503, 0.9896726));
    jp_ring_proximal = jp_ring_metacarpal + qrot(jq_ring_metacarpal, vec3(0.0013395, 0.0022744, -0.0583981)); jq_ring_proximal = qmul(jq_ring_metacarpal, vec4(-0.028627, -0.0569369, 0.0762824, 0.9950476));
    jq_ring_proximal = qmul(jq_ring_proximal, qaa(vec3(-0.9998552, 0.0149823, 0.0080695), fa.x * DEG));
    jq_ring_proximal = qmul(jq_ring_proximal, qaa(vec3(-0.0154377, -0.9980979, -0.0596845), fa.w * DEG));
    jp_ring_intermediate = jp_ring_proximal + qrot(jq_ring_proximal, vec3(-0.0005679, 0.0005542, -0.0428613)); jq_ring_intermediate = qmul(jq_ring_proximal, vec4(-0.0028307, -0.0200363, -0.0341962, 0.9992103));
    jq_ring_intermediate = qmul(jq_ring_intermediate, qaa(vec3(-0.9974115, -0.0535294, 0.0480099), fa.y * DEG));
    jp_ring_distal = jp_ring_intermediate + qrot(jq_ring_intermediate, vec3(-0.0006606, -0.0000309, -0.0265611)); jq_ring_distal = qmul(jq_ring_intermediate, vec4(0.02341, 0.0442362, -0.0052305, 0.9987331));
    jq_ring_distal = qmul(jq_ring_distal, qaa(vec3(-0.9972589, -0.0637315, -0.0375901), fa.z * DEG));
    jp_ring_tip = jp_ring_distal + qrot(jq_ring_distal, vec3(0.0000637, 0.0000171, -0.0119453)); jq_ring_tip = qmul(jq_ring_distal, vec4(-0.001798, 0.0004849, -0.0035096, 0.9999921));
    fa = fingerAng(4, t - 0.176, t);
    jp_pinky_metacarpal = jp_wrist + qrot(jq_wrist, vec3(0.023, -0.00942, -0.03408)); jq_pinky_metacarpal = qmul(jq_wrist, vec4(-0.025925, -0.1341248, -0.2095247, 0.9682137));
    jp_pinky_proximal = jp_pinky_metacarpal + qrot(jq_pinky_metacarpal, vec3(0.0014904, 0.0008353, -0.041788)); jq_pinky_proximal = qmul(jq_pinky_metacarpal, vec4(-0.0230977, 0.0121603, 0.0940017, 0.9952298));
    jq_pinky_proximal = qmul(jq_pinky_proximal, qaa(vec3(-0.992213, -0.1179944, -0.0398827), fa.x * DEG));
    jq_pinky_proximal = qmul(jq_pinky_proximal, qaa(vec3(0.121119, -0.9887245, -0.088057), fa.w * DEG));
    jp_pinky_intermediate = jp_pinky_proximal + qrot(jq_pinky_proximal, vec3(-0.000123, 0.0004905, -0.034578)); jq_pinky_intermediate = qmul(jq_pinky_proximal, vec4(0.0171229, -0.0522521, -0.0385596, 0.9977423));
    jq_pinky_intermediate = qmul(jq_pinky_intermediate, qaa(vec3(-0.97866, -0.1936683, 0.0686813), fa.y * DEG));
    jp_pinky_distal = jp_pinky_intermediate + qrot(jq_pinky_intermediate, vec3(0.0001798, -0.0000465, -0.0203119)); jq_pinky_distal = qmul(jq_pinky_intermediate, vec4(0.0230125, 0.0495571, 0.0014735, 0.9985051));
    jq_pinky_distal = qmul(jq_pinky_distal, qaa(vec3(-0.9816529, -0.1896484, -0.0197773), fa.z * DEG));
    jp_pinky_tip = jp_pinky_distal + qrot(jq_pinky_distal, vec3(0.0001183, 0.0000028, -0.0119055)); jq_pinky_tip = qmul(jq_pinky_distal, vec4(-0.0006724, -0.0291542, -0.0018551, 0.999573));
}

// ---------------------------------------------------------------- the orb
void orbSample(int k, out vec3 p) {
    if (k < 146) {
        if (k < 73) {
            if (k < 36) {
                if (k < 18) {
                    if (k < 9) {
                        if (k < 4) {
                            if (k < 2) {
                                if (k < 1) {
                                    p = vec3(0.085554, 0.9035973, 0.2328033);
                                } else {
                                    p = vec3(0.0855548, 0.9035963, 0.2328038);
                                }
                            } else {
                                if (k < 3) {
                                    p = vec3(0.0855605, 0.9035901, 0.2328073);
                                } else {
                                    p = vec3(0.0855717, 0.9035776, 0.2328143);
                                }
                            }
                        } else {
                            if (k < 6) {
                                if (k < 5) {
                                    p = vec3(0.0855879, 0.90356, 0.2328245);
                                } else {
                                    p = vec3(0.0856078, 0.9035385, 0.2328373);
                                }
                            } else {
                                if (k < 7) {
                                    p = vec3(0.0856303, 0.9035143, 0.232852);
                                } else {
                                    if (k < 8) {
                                        p = vec3(0.0856525, 0.9034867, 0.232868);
                                    } else {
                                        p = vec3(0.0856666, 0.9034495, 0.2328838);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 13) {
                            if (k < 11) {
                                if (k < 10) {
                                    p = vec3(0.0856593, 0.903391, 0.2328974);
                                } else {
                                    p = vec3(0.0856124, 0.9032941, 0.2329063);
                                }
                            } else {
                                if (k < 12) {
                                    p = vec3(0.0855032, 0.9031374, 0.2329077);
                                } else {
                                    p = vec3(0.0853063, 0.9028962, 0.2328988);
                                }
                            }
                        } else {
                            if (k < 15) {
                                if (k < 14) {
                                    p = vec3(0.0849935, 0.9025431, 0.2328775);
                                } else {
                                    p = vec3(0.0845348, 0.9020488, 0.2328424);
                                }
                            } else {
                                if (k < 16) {
                                    p = vec3(0.0838989, 0.901382, 0.2327941);
                                } else {
                                    if (k < 17) {
                                        p = vec3(0.0830538, 0.90051, 0.2327359);
                                    } else {
                                        p = vec3(0.081967, 0.8993984, 0.2326742);
                                    }
                                }
                            }
                        }
                    }
                } else {
                    if (k < 27) {
                        if (k < 22) {
                            if (k < 20) {
                                if (k < 19) {
                                    p = vec3(0.0806064, 0.8980113, 0.2326201);
                                } else {
                                    p = vec3(0.0789411, 0.8963107, 0.23259);
                                }
                            } else {
                                if (k < 21) {
                                    p = vec3(0.0769424, 0.894256, 0.2326071);
                                } else {
                                    p = vec3(0.0745854, 0.8918033, 0.2327014);
                                }
                            }
                        } else {
                            if (k < 24) {
                                if (k < 23) {
                                    p = vec3(0.0718504, 0.8889048, 0.2329106);
                                } else {
                                    p = vec3(0.0687254, 0.885508, 0.2332797);
                                }
                            } else {
                                if (k < 25) {
                                    p = vec3(0.0652075, 0.8815546, 0.2338596);
                                } else {
                                    if (k < 26) {
                                        p = vec3(0.0613053, 0.8769804, 0.2347054);
                                    } else {
                                        p = vec3(0.0570399, 0.8717156, 0.2358727);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 31) {
                            if (k < 29) {
                                if (k < 28) {
                                    p = vec3(0.0524454, 0.8656852, 0.2374133);
                                } else {
                                    p = vec3(0.0475685, 0.8588104, 0.2393713);
                                }
                            } else {
                                if (k < 30) {
                                    p = vec3(0.0424659, 0.8510113, 0.2417779);
                                } else {
                                    p = vec3(0.0372011, 0.8422088, 0.2446483);
                                }
                            }
                        } else {
                            if (k < 33) {
                                if (k < 32) {
                                    p = vec3(0.03184, 0.8323277, 0.2479799);
                                } else {
                                    p = vec3(0.0264463, 0.8212987, 0.2517524);
                                }
                            } else {
                                if (k < 34) {
                                    p = vec3(0.0210763, 0.8090605, 0.25593);
                                } else {
                                    if (k < 35) {
                                        p = vec3(0.0157754, 0.7955611, 0.2604651);
                                    } else {
                                        p = vec3(0.0105752, 0.7807579, 0.265303);
                                    }
                                }
                            }
                        }
                    }
                }
            } else {
                if (k < 54) {
                    if (k < 45) {
                        if (k < 40) {
                            if (k < 38) {
                                if (k < 37) {
                                    p = vec3(0.005492, 0.7646175, 0.270387);
                                } else {
                                    p = vec3(0.0005264, 0.7471154, 0.275662);
                                }
                            } else {
                                if (k < 39) {
                                    p = vec3(-0.0043354, 0.7282341, 0.281079);
                                } else {
                                    p = vec3(-0.0091214, 0.7079624, 0.2865963);
                                }
                            }
                        } else {
                            if (k < 42) {
                                if (k < 41) {
                                    p = vec3(-0.013871, 0.6862941, 0.2921819);
                                } else {
                                    p = vec3(-0.0186346, 0.6632264, 0.2978132);
                                }
                            } else {
                                if (k < 43) {
                                    p = vec3(-0.0234801, 0.6387615, 0.3034611);
                                } else {
                                    if (k < 44) {
                                        p = vec3(-0.0287388, 0.6129567, 0.3086073);
                                    } else {
                                        p = vec3(-0.0346944, 0.5858287, 0.3128303);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 49) {
                            if (k < 47) {
                                if (k < 46) {
                                    p = vec3(-0.0413907, 0.5573714, 0.3161359);
                                } else {
                                    p = vec3(-0.0487037, 0.5278788, 0.3186626);
                                }
                            } else {
                                if (k < 48) {
                                    p = vec3(-0.0564327, 0.4978454, 0.3205695);
                                } else {
                                    p = vec3(-0.064384, 0.4677517, 0.3219987);
                                }
                            }
                        } else {
                            if (k < 51) {
                                if (k < 50) {
                                    p = vec3(-0.0723965, 0.4379958, 0.3230656);
                                } else {
                                    p = vec3(-0.0803454, 0.4088839, 0.32386);
                                }
                            } else {
                                if (k < 52) {
                                    p = vec3(-0.0881378, 0.3806409, 0.3244505);
                                } else {
                                    if (k < 53) {
                                        p = vec3(-0.0957064, 0.3534263, 0.3248886);
                                    } else {
                                        p = vec3(-0.1030038, 0.3273484, 0.325213);
                                    }
                                }
                            }
                        }
                    }
                } else {
                    if (k < 63) {
                        if (k < 58) {
                            if (k < 56) {
                                if (k < 55) {
                                    p = vec3(-0.1099979, 0.302477, 0.3254525);
                                } else {
                                    p = vec3(-0.1166678, 0.2788524, 0.3256284);
                                }
                            } else {
                                if (k < 57) {
                                    p = vec3(-0.1230015, 0.2564933, 0.3257566);
                                } else {
                                    p = vec3(-0.128993, 0.2354018, 0.3258492);
                                }
                            }
                        } else {
                            if (k < 60) {
                                if (k < 59) {
                                    p = vec3(-0.1346413, 0.2155681, 0.3259147);
                                } else {
                                    p = vec3(-0.1399487, 0.1969734, 0.3259597);
                                }
                            } else {
                                if (k < 61) {
                                    p = vec3(-0.1449204, 0.1795922, 0.3259892);
                                } else {
                                    if (k < 62) {
                                        p = vec3(-0.1495632, 0.1633938, 0.3260066);
                                    } else {
                                        p = vec3(-0.1538857, 0.1483442, 0.3260146);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 68) {
                            if (k < 65) {
                                if (k < 64) {
                                    p = vec3(-0.1578972, 0.1344062, 0.3260154);
                                } else {
                                    p = vec3(-0.1616082, 0.121541, 0.3260102);
                                }
                            } else {
                                if (k < 66) {
                                    p = vec3(-0.1650293, 0.109708, 0.3260003);
                                } else {
                                    if (k < 67) {
                                        p = vec3(-0.1681721, 0.0988657, 0.3259864);
                                    } else {
                                        p = vec3(-0.171048, 0.0889714, 0.3259691);
                                    }
                                }
                            }
                        } else {
                            if (k < 70) {
                                if (k < 69) {
                                    p = vec3(-0.1736691, 0.0799823, 0.325949);
                                } else {
                                    p = vec3(-0.1760472, 0.0718547, 0.3259263);
                                }
                            } else {
                                if (k < 71) {
                                    p = vec3(-0.1781947, 0.0645448, 0.3259014);
                                } else {
                                    if (k < 72) {
                                        p = vec3(-0.1801239, 0.0580084, 0.3258743);
                                    } else {
                                        p = vec3(-0.181847, 0.0522013, 0.3258453);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        } else {
            if (k < 109) {
                if (k < 91) {
                    if (k < 82) {
                        if (k < 77) {
                            if (k < 75) {
                                if (k < 74) {
                                    p = vec3(-0.1833765, 0.0470789, 0.3258145);
                                } else {
                                    p = vec3(-0.1847248, 0.0425967, 0.325782);
                                }
                            } else {
                                if (k < 76) {
                                    p = vec3(-0.1859045, 0.0387102, 0.3257478);
                                } else {
                                    p = vec3(-0.1869281, 0.0353745, 0.325712);
                                }
                            }
                        } else {
                            if (k < 79) {
                                if (k < 78) {
                                    p = vec3(-0.187808, 0.0325451, 0.3256747);
                                } else {
                                    p = vec3(-0.1885569, 0.030177, 0.3256359);
                                }
                            } else {
                                if (k < 80) {
                                    p = vec3(-0.1891873, 0.0282256, 0.3255957);
                                } else {
                                    if (k < 81) {
                                        p = vec3(-0.1897117, 0.026646, 0.3255541);
                                    } else {
                                        p = vec3(-0.1901426, 0.0253934, 0.325511);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 86) {
                            if (k < 84) {
                                if (k < 83) {
                                    p = vec3(-0.1904928, 0.024423, 0.3254667);
                                } else {
                                    p = vec3(-0.1907747, 0.02369, 0.325421);
                                }
                            } else {
                                if (k < 85) {
                                    p = vec3(-0.1910008, 0.0231497, 0.3253741);
                                } else {
                                    p = vec3(-0.1911831, 0.0227598, 0.325326);
                                }
                            }
                        } else {
                            if (k < 88) {
                                if (k < 87) {
                                    p = vec3(-0.1913316, 0.0224844, 0.3252766);
                                } else {
                                    p = vec3(-0.1914543, 0.0222948, 0.3252261);
                                }
                            } else {
                                if (k < 89) {
                                    p = vec3(-0.1915574, 0.022169, 0.3251744);
                                } else {
                                    if (k < 90) {
                                        p = vec3(-0.1916454, 0.0220903, 0.3251217);
                                    } else {
                                        p = vec3(-0.1917219, 0.022046, 0.3250679);
                                    }
                                }
                            }
                        }
                    }
                } else {
                    if (k < 100) {
                        if (k < 95) {
                            if (k < 93) {
                                if (k < 92) {
                                    p = vec3(-0.1917895, 0.0220268, 0.3250131);
                                } else {
                                    p = vec3(-0.1918501, 0.0220257, 0.3249573);
                                }
                            } else {
                                if (k < 94) {
                                    p = vec3(-0.1919051, 0.0220373, 0.3249006);
                                } else {
                                    p = vec3(-0.1919555, 0.0220578, 0.324843);
                                }
                            }
                        } else {
                            if (k < 97) {
                                if (k < 96) {
                                    p = vec3(-0.1920021, 0.0220842, 0.3247846);
                                } else {
                                    p = vec3(-0.1920454, 0.0221144, 0.3247253);
                                }
                            } else {
                                if (k < 98) {
                                    p = vec3(-0.1920859, 0.0221467, 0.3246653);
                                } else {
                                    if (k < 99) {
                                        p = vec3(-0.1921239, 0.0221799, 0.3246046);
                                    } else {
                                        p = vec3(-0.1921594, 0.0222131, 0.3245432);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 104) {
                            if (k < 102) {
                                if (k < 101) {
                                    p = vec3(-0.1921926, 0.0222454, 0.3244812);
                                } else {
                                    p = vec3(-0.1922224, 0.0222757, 0.3244187);
                                }
                            } else {
                                if (k < 103) {
                                    p = vec3(-0.1922475, 0.022303, 0.3243561);
                                } else {
                                    p = vec3(-0.1922656, 0.0223254, 0.3242935);
                                }
                            }
                        } else {
                            if (k < 106) {
                                if (k < 105) {
                                    p = vec3(-0.1922739, 0.0223413, 0.3242314);
                                } else {
                                    p = vec3(-0.1922692, 0.0223488, 0.3241701);
                                }
                            } else {
                                if (k < 107) {
                                    p = vec3(-0.1922482, 0.0223456, 0.3241101);
                                } else {
                                    if (k < 108) {
                                        p = vec3(-0.1922069, 0.0223297, 0.3240519);
                                    } else {
                                        p = vec3(-0.1921415, 0.0222988, 0.323996);
                                    }
                                }
                            }
                        }
                    }
                }
            } else {
                if (k < 127) {
                    if (k < 118) {
                        if (k < 113) {
                            if (k < 111) {
                                if (k < 110) {
                                    p = vec3(-0.1920478, 0.0222506, 0.3239429);
                                } else {
                                    p = vec3(-0.1919217, 0.0221827, 0.3238931);
                                }
                            } else {
                                if (k < 112) {
                                    p = vec3(-0.1917589, 0.0220929, 0.3238472);
                                } else {
                                    p = vec3(-0.191555, 0.021979, 0.3238059);
                                }
                            }
                        } else {
                            if (k < 115) {
                                if (k < 114) {
                                    p = vec3(-0.1913054, 0.0218387, 0.3237697);
                                } else {
                                    p = vec3(-0.1910058, 0.0216698, 0.3237392);
                                }
                            } else {
                                if (k < 116) {
                                    p = vec3(-0.1906515, 0.0214704, 0.3237153);
                                } else {
                                    if (k < 117) {
                                        p = vec3(-0.1902377, 0.0212383, 0.3236985);
                                    } else {
                                        p = vec3(-0.1897599, 0.0209718, 0.3236897);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 122) {
                            if (k < 120) {
                                if (k < 119) {
                                    p = vec3(-0.189213, 0.0206692, 0.3236896);
                                } else {
                                    p = vec3(-0.1885922, 0.0203289, 0.3236992);
                                }
                            } else {
                                if (k < 121) {
                                    p = vec3(-0.187895, 0.0199509, 0.323719);
                                } else {
                                    p = vec3(-0.1871365, 0.019544, 0.3237476);
                                }
                            }
                        } else {
                            if (k < 124) {
                                if (k < 123) {
                                    p = vec3(-0.186343, 0.0191219, 0.3237817);
                                } else {
                                    p = vec3(-0.1855401, 0.0186978, 0.3238183);
                                }
                            } else {
                                if (k < 125) {
                                    p = vec3(-0.1847488, 0.018282, 0.3238548);
                                } else {
                                    if (k < 126) {
                                        p = vec3(-0.1839849, 0.0178818, 0.3238891);
                                    } else {
                                        p = vec3(-0.1832596, 0.0175022, 0.3239199);
                                    }
                                }
                            }
                        }
                    }
                } else {
                    if (k < 136) {
                        if (k < 131) {
                            if (k < 129) {
                                if (k < 128) {
                                    p = vec3(-0.1825804, 0.0171464, 0.323946);
                                } else {
                                    p = vec3(-0.1825804, 0.0171464, 0.323946);
                                }
                            } else {
                                if (k < 130) {
                                    p = vec3(-0.2441128, -0.2274464, 0.3606881);
                                } else {
                                    p = vec3(-0.2441203, -0.2274456, 0.3606903);
                                }
                            }
                        } else {
                            if (k < 133) {
                                if (k < 132) {
                                    p = vec3(-0.2441427, -0.2274434, 0.3606968);
                                } else {
                                    p = vec3(-0.2441805, -0.2274401, 0.3607077);
                                }
                            } else {
                                if (k < 134) {
                                    p = vec3(-0.2442322, -0.2274362, 0.3607224);
                                } else {
                                    if (k < 135) {
                                        p = vec3(-0.2442959, -0.227432, 0.3607402);
                                    } else {
                                        p = vec3(-0.2443697, -0.2274279, 0.3607604);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 141) {
                            if (k < 138) {
                                if (k < 137) {
                                    p = vec3(-0.244452, -0.2274241, 0.3607826);
                                } else {
                                    p = vec3(-0.2445416, -0.2274208, 0.3608063);
                                }
                            } else {
                                if (k < 139) {
                                    p = vec3(-0.244636, -0.227418, 0.3608311);
                                } else {
                                    if (k < 140) {
                                        p = vec3(-0.2447237, -0.2274156, 0.3608568);
                                    } else {
                                        p = vec3(-0.2447799, -0.2274131, 0.3608832);
                                    }
                                }
                            }
                        } else {
                            if (k < 143) {
                                if (k < 142) {
                                    p = vec3(-0.2447658, -0.2274098, 0.3609099);
                                } else {
                                    p = vec3(-0.2446313, -0.2274047, 0.3609369);
                                }
                            } else {
                                if (k < 144) {
                                    p = vec3(-0.2443171, -0.2273962, 0.3609638);
                                } else {
                                    if (k < 145) {
                                        p = vec3(-0.2437565, -0.2273824, 0.3609904);
                                    } else {
                                        p = vec3(-0.2428771, -0.2273601, 0.3610164);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    } else {
        if (k < 219) {
            if (k < 182) {
                if (k < 164) {
                    if (k < 155) {
                        if (k < 150) {
                            if (k < 148) {
                                if (k < 147) {
                                    p = vec3(-0.2416022, -0.227325, 0.361041);
                                } else {
                                    p = vec3(-0.239851, -0.2272705, 0.3610637);
                                }
                            } else {
                                if (k < 149) {
                                    p = vec3(-0.2375395, -0.2271873, 0.3610831);
                                } else {
                                    p = vec3(-0.2345808, -0.2270621, 0.3610978);
                                }
                            }
                        } else {
                            if (k < 152) {
                                if (k < 151) {
                                    p = vec3(-0.2308851, -0.2268768, 0.3611055);
                                } else {
                                    p = vec3(-0.2263599, -0.2266072, 0.3611033);
                                }
                            } else {
                                if (k < 153) {
                                    p = vec3(-0.2209102, -0.2262211, 0.3610876);
                                } else {
                                    if (k < 154) {
                                        p = vec3(-0.2144386, -0.2256769, 0.3610535);
                                    } else {
                                        p = vec3(-0.2068456, -0.2249215, 0.360995);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 159) {
                            if (k < 157) {
                                if (k < 156) {
                                    p = vec3(-0.1980326, -0.223888, 0.3609049);
                                } else {
                                    p = vec3(-0.1880172, -0.222481, 0.3607846);
                                }
                            } else {
                                if (k < 158) {
                                    p = vec3(-0.1770344, -0.220584, 0.3606539);
                                } else {
                                    p = vec3(-0.1653696, -0.2181003, 0.3605393);
                                }
                            }
                        } else {
                            if (k < 161) {
                                if (k < 160) {
                                    p = vec3(-0.15327, -0.214965, 0.3604665);
                                } else {
                                    p = vec3(-0.1409214, -0.2111409, 0.360458);
                                }
                            } else {
                                if (k < 162) {
                                    p = vec3(-0.1284497, -0.206611, 0.3605323);
                                } else {
                                    if (k < 163) {
                                        p = vec3(-0.1159301, -0.2013705, 0.3607044);
                                    } else {
                                        p = vec3(-0.1033969, -0.195421, 0.3609859);
                                    }
                                }
                            }
                        }
                    }
                } else {
                    if (k < 173) {
                        if (k < 168) {
                            if (k < 166) {
                                if (k < 165) {
                                    p = vec3(-0.0908526, -0.1887659, 0.3613858);
                                } else {
                                    p = vec3(-0.0782757, -0.1814076, 0.3619105);
                                }
                            } else {
                                if (k < 167) {
                                    p = vec3(-0.0656266, -0.1733457, 0.3625644);
                                } else {
                                    p = vec3(-0.052852, -0.1645758, 0.3633505);
                                }
                            }
                        } else {
                            if (k < 170) {
                                if (k < 169) {
                                    p = vec3(-0.0398885, -0.1550895, 0.3642703);
                                } else {
                                    p = vec3(-0.0266651, -0.1448738, 0.3653242);
                                }
                            } else {
                                if (k < 171) {
                                    p = vec3(-0.0131073, -0.133914, 0.3665116);
                                } else {
                                    if (k < 172) {
                                        p = vec3(0.0007699, -0.1222798, 0.36782);
                                    } else {
                                        p = vec3(0.0147815, -0.1102009, 0.3692156);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 177) {
                            if (k < 175) {
                                if (k < 174) {
                                    p = vec3(0.0287083, -0.0979386, 0.3706599);
                                } else {
                                    p = vec3(0.0423661, -0.0857206, 0.3721184);
                                }
                            } else {
                                if (k < 176) {
                                    p = vec3(0.0556161, -0.0737255, 0.373563);
                                } else {
                                    p = vec3(0.068359, -0.0620862, 0.3749724);
                                }
                            }
                        } else {
                            if (k < 179) {
                                if (k < 178) {
                                    p = vec3(0.0805264, -0.0508978, 0.3763309);
                                } else {
                                    p = vec3(0.0920703, -0.0402253, 0.3776282);
                                }
                            } else {
                                if (k < 180) {
                                    p = vec3(0.1029562, -0.0301117, 0.3788578);
                                } else {
                                    if (k < 181) {
                                        p = vec3(0.1131581, -0.0205828, 0.3800165);
                                    } else {
                                        p = vec3(0.122656, -0.0116518, 0.3811035);
                                    }
                                }
                            }
                        }
                    }
                }
            } else {
                if (k < 200) {
                    if (k < 191) {
                        if (k < 186) {
                            if (k < 184) {
                                if (k < 183) {
                                    p = vec3(0.131434, -0.0033226, 0.3821202);
                                } else {
                                    p = vec3(0.1394794, 0.0044082, 0.3830689);
                                }
                            } else {
                                if (k < 185) {
                                    p = vec3(0.1467817, 0.0115493, 0.3839534);
                                } else {
                                    p = vec3(0.153332, 0.0181137, 0.384778);
                                }
                            }
                        } else {
                            if (k < 188) {
                                if (k < 187) {
                                    p = vec3(0.1591225, 0.0241178, 0.3855476);
                                } else {
                                    p = vec3(0.1641464, 0.0295809, 0.3862675);
                                }
                            } else {
                                if (k < 189) {
                                    p = vec3(0.1683974, 0.0345249, 0.3869431);
                                } else {
                                    if (k < 190) {
                                        p = vec3(0.1718698, 0.0389741, 0.3875801);
                                    } else {
                                        p = vec3(0.1745579, 0.0429554, 0.3881842);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 195) {
                            if (k < 193) {
                                if (k < 192) {
                                    p = vec3(0.1764567, 0.046498, 0.388761);
                                } else {
                                    p = vec3(0.1775611, 0.0496344, 0.3893159);
                                }
                            } else {
                                if (k < 194) {
                                    p = vec3(0.1778664, 0.0524, 0.3898543);
                                } else {
                                    p = vec3(0.1773683, 0.054834, 0.3903813);
                                }
                            }
                        } else {
                            if (k < 197) {
                                if (k < 196) {
                                    p = vec3(0.1760629, 0.0569794, 0.3909014);
                                } else {
                                    p = vec3(0.1739469, 0.0588841, 0.391419);
                                }
                            } else {
                                if (k < 198) {
                                    p = vec3(0.1710182, 0.0606009, 0.3919378);
                                } else {
                                    if (k < 199) {
                                        p = vec3(0.167276, 0.0621883, 0.3924609);
                                    } else {
                                        p = vec3(0.1627215, 0.0637112, 0.3929907);
                                    }
                                }
                            }
                        }
                    }
                } else {
                    if (k < 209) {
                        if (k < 204) {
                            if (k < 202) {
                                if (k < 201) {
                                    p = vec3(0.1573598, 0.065241, 0.3935287);
                                } else {
                                    p = vec3(0.1512758, 0.0668375, 0.3940699);
                                }
                            } else {
                                if (k < 203) {
                                    p = vec3(0.144697, 0.0685216, 0.3945995);
                                } else {
                                    p = vec3(0.1378774, 0.0702891, 0.3951024);
                                }
                            }
                        } else {
                            if (k < 206) {
                                if (k < 205) {
                                    p = vec3(0.1310428, 0.0721281, 0.3955674);
                                } else {
                                    p = vec3(0.1243922, 0.0740418, 0.3959878);
                                }
                            } else {
                                if (k < 207) {
                                    p = vec3(0.118111, 0.0760639, 0.3963604);
                                } else {
                                    if (k < 208) {
                                        p = vec3(0.1123779, 0.078261, 0.3966843);
                                    } else {
                                        p = vec3(0.1073678, 0.0807288, 0.3969606);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 214) {
                            if (k < 211) {
                                if (k < 210) {
                                    p = vec3(0.1032541, 0.0835882, 0.3971917);
                                } else {
                                    p = vec3(0.1002093, 0.0869806, 0.3973804);
                                }
                            } else {
                                if (k < 212) {
                                    p = vec3(0.0984058, 0.0910645, 0.3975302);
                                } else {
                                    if (k < 213) {
                                        p = vec3(0.0980163, 0.0960123, 0.3976444);
                                    } else {
                                        p = vec3(0.0992142, 0.1020077, 0.3977267);
                                    }
                                }
                            }
                        } else {
                            if (k < 216) {
                                if (k < 215) {
                                    p = vec3(0.1021736, 0.1092437, 0.3977802);
                                } else {
                                    p = vec3(0.1070698, 0.1179212, 0.3978083);
                                }
                            } else {
                                if (k < 217) {
                                    p = vec3(0.1140786, 0.128247, 0.3978139);
                                } else {
                                    if (k < 218) {
                                        p = vec3(0.123377, 0.1404332, 0.3977996);
                                    } else {
                                        p = vec3(0.135143, 0.1546962, 0.3977682);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        } else {
            if (k < 256) {
                if (k < 237) {
                    if (k < 228) {
                        if (k < 223) {
                            if (k < 221) {
                                if (k < 220) {
                                    p = vec3(0.149555, 0.1712556, 0.3977219);
                                } else {
                                    p = vec3(0.1667924, 0.1903339, 0.397663);
                                }
                            } else {
                                if (k < 222) {
                                    p = vec3(0.1869214, 0.2121012, 0.3973646);
                                } else {
                                    p = vec3(0.2091904, 0.2361229, 0.395534);
                                }
                            }
                        } else {
                            if (k < 225) {
                                if (k < 224) {
                                    p = vec3(0.2326962, 0.261431, 0.3918323);
                                } else {
                                    p = vec3(0.2567066, 0.2870253, 0.3866209);
                                }
                            } else {
                                if (k < 226) {
                                    p = vec3(0.2806435, 0.3120688, 0.3804335);
                                } else {
                                    if (k < 227) {
                                        p = vec3(0.3040487, 0.3359325, 0.3737774);
                                    } else {
                                        p = vec3(0.3265562, 0.358183, 0.3670674);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 232) {
                            if (k < 230) {
                                if (k < 229) {
                                    p = vec3(0.3478715, 0.3785507, 0.3606115);
                                } else {
                                    p = vec3(0.3677615, 0.3968954, 0.3546191);
                                }
                            } else {
                                if (k < 231) {
                                    p = vec3(0.3860485, 0.413175, 0.3492164);
                                } else {
                                    p = vec3(0.402609, 0.427418, 0.3444651);
                                }
                            }
                        } else {
                            if (k < 234) {
                                if (k < 233) {
                                    p = vec3(0.4173721, 0.4397038, 0.3403794);
                                } else {
                                    p = vec3(0.4303161, 0.4501464, 0.3369365);
                                }
                            } else {
                                if (k < 235) {
                                    p = vec3(0.4414642, 0.458884, 0.3340838);
                                } else {
                                    if (k < 236) {
                                        p = vec3(0.450881, 0.4660702, 0.3317476);
                                    } else {
                                        p = vec3(0.4586685, 0.4718696, 0.3298415);
                                    }
                                }
                            }
                        }
                    }
                } else {
                    if (k < 246) {
                        if (k < 241) {
                            if (k < 239) {
                                if (k < 238) {
                                    p = vec3(0.4649607, 0.4764533, 0.3282734);
                                } else {
                                    p = vec3(0.4699184, 0.4799979, 0.3269494);
                                }
                            } else {
                                if (k < 240) {
                                    p = vec3(0.4737231, 0.4826842, 0.3257766);
                                } else {
                                    p = vec3(0.4765658, 0.4846926, 0.324665);
                                }
                            }
                        } else {
                            if (k < 243) {
                                if (k < 242) {
                                    p = vec3(0.4786167, 0.4861833, 0.3235341);
                                } else {
                                    p = vec3(0.4800018, 0.4872828, 0.3223172);
                                }
                            } else {
                                if (k < 244) {
                                    p = vec3(0.4807986, 0.4880862, 0.3209625);
                                } else {
                                    if (k < 245) {
                                        p = vec3(0.4810379, 0.4886641, 0.3194336);
                                    } else {
                                        p = vec3(0.4807062, 0.489071, 0.3177119);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 251) {
                            if (k < 248) {
                                if (k < 247) {
                                    p = vec3(0.4797485, 0.489352, 0.3158026);
                                } else {
                                    p = vec3(0.4780706, 0.4895486, 0.3137471);
                                }
                            } else {
                                if (k < 249) {
                                    p = vec3(0.4755479, 0.4897002, 0.3116424);
                                } else {
                                    if (k < 250) {
                                        p = vec3(0.4720465, 0.4898371, 0.3096724);
                                    } else {
                                        p = vec3(0.4674697, 0.4899583, 0.3081432);
                                    }
                                }
                            }
                        } else {
                            if (k < 253) {
                                if (k < 252) {
                                    p = vec3(0.4618374, 0.4899863, 0.3075062);
                                } else {
                                    p = vec3(0.4553868, 0.4897021, 0.3083278);
                                }
                            } else {
                                if (k < 254) {
                                    p = vec3(0.4486418, 0.4886962, 0.311168);
                                } else {
                                    if (k < 255) {
                                        p = vec3(0.4423735, 0.4863906, 0.3163855);
                                    } else {
                                        p = vec3(0.4374182, 0.4821598, 0.323979);
                                    }
                                }
                            }
                        }
                    }
                }
            } else {
                if (k < 274) {
                    if (k < 265) {
                        if (k < 260) {
                            if (k < 258) {
                                if (k < 257) {
                                    p = vec3(0.434381, 0.4755019, 0.3335223);
                                } else {
                                    p = vec3(0.4333203, 0.4661656, 0.3442478);
                                }
                            } else {
                                if (k < 259) {
                                    p = vec3(0.433804, 0.4541176, 0.3554169);
                                } else {
                                    p = vec3(0.4347735, 0.4394417, 0.366257);
                                }
                            }
                        } else {
                            if (k < 262) {
                                if (k < 261) {
                                    p = vec3(0.4343487, 0.4222026, 0.3757156);
                                } else {
                                    p = vec3(0.4316879, 0.4023784, 0.383607);
                                }
                            } else {
                                if (k < 263) {
                                    p = vec3(0.4264637, 0.3799959, 0.3901381);
                                } else {
                                    if (k < 264) {
                                        p = vec3(0.4184831, 0.355237, 0.3956098);
                                    } else {
                                        p = vec3(0.4074996, 0.3285337, 0.4002894);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 269) {
                            if (k < 267) {
                                if (k < 266) {
                                    p = vec3(0.3931067, 0.3006245, 0.4043595);
                                } else {
                                    p = vec3(0.374845, 0.2723103, 0.4079443);
                                }
                            } else {
                                if (k < 268) {
                                    p = vec3(0.3526912, 0.2442807, 0.4111309);
                                } else {
                                    p = vec3(0.3271779, 0.2171106, 0.4139821);
                                }
                            }
                        } else {
                            if (k < 271) {
                                if (k < 270) {
                                    p = vec3(0.2992122, 0.1912563, 0.4165442);
                                } else {
                                    p = vec3(0.2698559, 0.1670557, 0.4188518);
                                }
                            } else {
                                if (k < 272) {
                                    p = vec3(0.2401544, 0.1447357, 0.4209319);
                                } else {
                                    if (k < 273) {
                                        p = vec3(0.2110279, 0.1244234, 0.4228054);
                                    } else {
                                        p = vec3(0.1832154, 0.1061621, 0.4244897);
                                    }
                                }
                            }
                        }
                    }
                } else {
                    if (k < 283) {
                        if (k < 278) {
                            if (k < 276) {
                                if (k < 275) {
                                    p = vec3(0.1572588, 0.0899268, 0.4259993);
                                } else {
                                    p = vec3(0.1335105, 0.0756405, 0.4273469);
                                }
                            } else {
                                if (k < 277) {
                                    p = vec3(0.1121571, 0.0631887, 0.4285439);
                                } else {
                                    p = vec3(0.093249, 0.0524326, 0.4296008);
                                }
                            }
                        } else {
                            if (k < 280) {
                                if (k < 279) {
                                    p = vec3(0.076732, 0.0432193, 0.4305277);
                                } else {
                                    p = vec3(0.0624771, 0.0353913, 0.4313344);
                                }
                            } else {
                                if (k < 281) {
                                    p = vec3(0.0503068, 0.028793, 0.4320303);
                                } else {
                                    if (k < 282) {
                                        p = vec3(0.0400169, 0.0232751, 0.4326248);
                                    } else {
                                        p = vec3(0.0313933, 0.0186986, 0.4331273);
                                    }
                                }
                            }
                        }
                    } else {
                        if (k < 288) {
                            if (k < 285) {
                                if (k < 284) {
                                    p = vec3(0.0242253, 0.0149364, 0.4335469);
                                } else {
                                    p = vec3(0.0183131, 0.0118739, 0.433893);
                                }
                            } else {
                                if (k < 286) {
                                    p = vec3(0.0134741, 0.0094095, 0.4341749);
                                } else {
                                    if (k < 287) {
                                        p = vec3(0.0095442, 0.0074531, 0.4344018);
                                    } else {
                                        p = vec3(0.0063791, 0.0059257, 0.4345829);
                                    }
                                }
                            }
                        } else {
                            if (k < 290) {
                                if (k < 289) {
                                    p = vec3(0.0038689, 0.0047565, 0.4347305);
                                } else {
                                    p = vec3(0.0020134, 0.0038772, 0.4348731);
                                }
                            } else {
                                if (k < 291) {
                                    p = vec3(0.0009729, 0.0032186, 0.4350643);
                                } else {
                                    if (k < 292) {
                                        p = vec3(0.0008498, 0.0029506, 0.4352012);
                                    } else {
                                        p = vec3(0.0008498, 0.0029506, 0.4352012);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
vec3 orbPath(int base, float x) {
    int k = int(floor(x));
    vec3 p0, p1;
    orbSample(base + k, p0); orbSample(base + k + 1, p1);
    return mix(p0, p1, x - float(k));
}
// position, squash (x, y) and brightness; on = 0 when there is no orb
void orbState(float t, out vec3 p, out vec2 sc, out float on) {
    on = 0.0; sc = vec2(1.0); p = vec3(0.0, 0.0, -9.0);
    if (t >= 4.75 && t < 6.86) { p = orbPath(0, (t - 4.75) * 60.0); on = 1.0; return; }
    if (t >= 9.3 && t < 12.0) {
        p = orbPath(129, (t - 9.3) * 60.0); on = 1.0;
        sc = vec2(easeIO(clamp((t - 9.3) / 0.14, 0.0, 1.0)));
        return;
    }
    if (t >= 6.86 && t < 7.34) {
        float u = clamp((t - 6.86) / 0.36, 0.0, 1.0), u2 = u * u, u3 = u2 * u;
        p = vec3(-0.1825804, 0.0171464, 0.323946) * (2.0 * u3 - 3.0 * u2 + 1.0) + vec3(0.0144069, -0.0075515, 0.0005365) * (u3 - 2.0 * u2 + u) + vec3(-0.3, -0.205, 0.095) * (-2.0 * u3 + 3.0 * u2) + vec3(0.0, -0.495, 0.0) * (u3 - u2);
        float land = t - 7.22;
        on = 1.0;
        if (land >= 0.0) { float k = clamp(land / 0.12, 0.0, 1.0), s = sin(PI * min(1.0, k * 2.0)); sc = vec2(1.0 + 0.28 * s, 1.0 - 0.3 * s); on = 1.0 - easeIO(k); }
        else if (u > 0.85) { float k = (u - 0.85) / 0.15; sc = vec2(1.0 - 0.08 * k, 1.0 + 0.12 * k); }
        sc *= max(0.05, on);
        return;
    }
    if (t >= 12.0 && t < 12.54) {
        float u = clamp((t - 12.0) / 0.42, 0.0, 1.0), u2 = u * u, u3 = u2 * u;
        p = vec3(0.0010528, 0.0027089, 0.4353797) * (2.0 * u3 - 3.0 * u2 + 1.0) + vec3(0.0102297, -0.0121801, 0.0089949) * (u3 - 2.0 * u2 + u) + vec3(-0.3, -0.205, 0.095) * (-2.0 * u3 + 3.0 * u2) + vec3(0.0275, -0.605, 0.0) * (u3 - u2);
        float land = t - 12.42;
        on = 1.0;
        if (land >= 0.0) { float k = clamp(land / 0.12, 0.0, 1.0), s = sin(PI * min(1.0, k * 2.0)); sc = vec2(1.0 + 0.28 * s, 1.0 - 0.3 * s); on = 1.0 - easeIO(k); }
        else if (u > 0.85) { float k = (u - 0.85) / 0.15; sc = vec2(1.0 - 0.08 * k, 1.0 + 0.12 * k); }
        sc *= max(0.05, on);
        return;
    }
}

// ---------------------------------------------------------------- the lamp
void lampStateData(int k, out vec3 core, out vec3 hc, out float on, out float tint) {
    if (k < 2) {
        if (k < 1) {
            core = vec3(0.0, 0.0, 0.0); hc = vec3(0.0, 0.0, 0.0); on = 0.0; tint = 0.0;
        } else {
            core = vec3(1.0628536, 0.0523054, 0.0); hc = vec3(0.5471481, 0.0590686, 0.008574); on = 1.0; tint = 0.15;
        }
    } else {
        if (k < 3) {
            core = vec3(0.765337, 0.0038347, 0.0057248); hc = vec3(0.2273927, 0.0240445, 0.016209); on = 1.0; tint = 1.0;
        } else {
            if (k < 4) {
                core = vec3(0.0, 0.4874642, 0.0325552); hc = vec3(0.0168047, 0.2605346, 0.0601662); on = 1.0; tint = 0.9;
            } else {
                core = vec3(1.7306356, 0.4356416, 0.1081143); hc = vec3(0.7944506, 0.2123415, 0.0521841); on = 1.0; tint = 0.35;
            }
        }
    }
}
void lampEvent(int k, out vec3 ev) {
    if (k < 4) {
        if (k < 2) {
            if (k < 1) {
                ev = vec3(0.0, 0.0, 0.15);
            } else {
                ev = vec3(7.22, 1.0, 0.15);
            }
        } else {
            if (k < 3) {
                ev = vec3(9.3, 0.0, 0.15);
            } else {
                ev = vec3(12.42, 1.0, 0.15);
            }
        }
    } else {
        if (k < 6) {
            if (k < 5) {
                ev = vec3(14.58, 2.0, 0.15);
            } else {
                ev = vec3(16.08, 3.0, 0.15);
            }
        } else {
            if (k < 7) {
                ev = vec3(17.58, 4.0, 0.15);
            } else {
                ev = vec3(19.5, 0.0, 0.45);
            }
        }
    }
}
void lampState(float t, out vec3 core, out vec3 hc, out float I, out float tint, out float flame) {
    int kk = 0;
    vec3 ev;
    for (int i = 0; i < 8; i++) { lampEvent(i, ev); if (t >= ev.x) kk = i; }
    vec3 cur;
    lampEvent(kk, cur);
    vec3 pe = vec3(0.0);
    if (kk > 0) lampEvent(kk - 1, pe);
    vec3 pCore, pHalo, cCore, cHalo; float pOn, pTint, cOn, cTint;
    lampStateData(int(pe.y + 0.5), pCore, pHalo, pOn, pTint);
    lampStateData(int(cur.y + 0.5), cCore, cHalo, cOn, cTint);
    float u = clamp((t - cur.x) / cur.z, 0.0, 1.0), e = easeIO(u);
    vec3 fromCore = pOn > 0.5 ? pCore : cCore, toCore = cOn > 0.5 ? cCore : pCore;
    vec3 fromHalo = pOn > 0.5 ? pHalo : cHalo, toHalo = cOn > 0.5 ? cHalo : pHalo;
    core = mix(fromCore, toCore, e); hc = mix(fromHalo, toHalo, e);
    I = pOn + (cOn - pOn) * e;
    if (cOn > 0.5 && kk > 0) { float k = clamp((t - cur.x) / 0.2, 0.0, 1.0); I += 0.38 * sin(PI * k) * (1.0 - k) * 1.6; }   // bulb kick
    tint = pTint + (cTint - pTint) * e;
    I *= 1.0 + 0.035 * sin(t * 31.0) * sin(t * 17.3 + 1.0);
    flame = I > 0.02 ? min(1.0, I) : 0.0;
}

float sdRR(vec2 p, vec2 b, float r) { vec2 q = abs(p) - b + r; return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r; }

// The sconce: a woven rounded-rectangle shade, backplate and stem, flame. alpha = coverage.
vec4 lampShade(vec2 p, float pw, vec3 core, float I, float flameK, float calibK, bool lightOnly, out vec3 bloom) {
    bloom = vec3(0.0);
    vec2 sp = p - vec2(0.0, -0.045);
    float d = sdRR(sp, SHADE_HALF - 0.003, 0.03);
    float aa = pw;
    float shade = 1.0 - smoothstep(-aa, aa, d);
    float plate = 1.0 - smoothstep(-aa, aa, sdRR(p - vec2(0.0, 0.075), vec2(0.026, 0.016), 0.006));
    float stem = 1.0 - smoothstep(-aa, aa, sdRR(p - vec2(0.0, 0.040), vec2(0.004, 0.024), 0.002));
    float metal = max(plate, stem) * (1.0 - shade);
    if (shade + metal < 0.002) return vec4(0.0);
    // basket weave: alternating horizontal and vertical strands
    vec2 wv = sp / 0.011;
    vec2 cell = floor(wv), fr = fract(wv);
    float across = mod(cell.x + cell.y, 2.0) < 0.5 ? fr.y : fr.x;
    float strand = smoothstep(0.0, 0.16, across) * (1.0 - smoothstep(0.84, 1.0, across));
    vec3 base = C_WEAVE * (0.82 + 0.26 * sin(PI * across)) * mix(0.6, 1.0, strand);
    base *= 1.0 - 0.28 * smoothstep(-0.012, 0.0, d);
    vec2 lc = sp / SHADE_HALF * vec2(0.9, 1.25);
    float inner = exp(-dot(lc, lc) * 1.1);
    vec3 emis = core * I * (0.55 + 0.45 * inner) * (0.78 * strand + 1.0 * (1.0 - strand));
    float fl = 0.0;
    if (flameK > 0.001) {
        float t = gT;
        vec2 fp = sp - vec2(0.004 * (noise1(t * 7.0) - 0.5), -0.022);
        float hgt = 0.045 * (0.88 + 0.22 * noise1(t * 9.0 + 3.0));
        float yy = clamp(fp.y / hgt, -0.4, 1.2);
        float wdt = 0.017 * (1.0 - smoothstep(0.0, 1.0, yy)) * (0.9 + 0.2 * noise1(t * 13.0)) + 0.002;
        float fd = abs(fp.x + 0.004 * sin(fp.y * 120.0 + t * 14.0) * yy) - wdt;
        fd = max(fd, max(-fp.y, fp.y - hgt));
        fd = min(fd, length(fp - vec2(0.0, 0.002)) - 0.0175 * (0.94 + 0.12 * noise1(t * 11.0)));
        fl = (1.0 - smoothstep(-aa, aa, fd)) * flameK;
    }
    vec3 flameCol = mix(core, vec3(1.0, 0.85, 0.55), 0.1) * 0.9 * fl * (0.55 + 0.45 * (1.0 - strand));
    vec3 metalCol = vec3(0.09, 0.08, 0.06) * (0.8 + 0.4 * smoothstep(0.0, 0.03, p.y - 0.05));
    base *= 1.0 - 0.93 * clamp(I, 0.0, 1.0);
    vec3 calib = C_MINT * calibK * 0.55 * (shade + metal);
    bloom = (emis * 0.3 + flameCol * 0.4) * shade + calib * 0.3;
    if (lightOnly) return vec4((emis + flameCol) * shade + calib, shade + metal);   // the real sconce takes only the light
    return vec4((base + emis + flameCol) * shade + metalCol * metal + calib, 1.0);   // opaque, like the scene's lamp card: its anti-aliased edge reads as a thin dark outline
}

// ---------------------------------------------------------------- the calibration grid
// unit square -> quad (BL, BR, TR, TL); returns the inverse as three rows
void gridInverse(vec2 p0, vec2 p1, vec2 p2, vec2 p3, out vec3 i0, out vec3 i1, out vec3 i2) {
    float dx1 = p1.x - p2.x, dx2 = p3.x - p2.x, dy1 = p1.y - p2.y, dy2 = p3.y - p2.y;
    float sx = p0.x - p1.x + p2.x - p3.x, sy = p0.y - p1.y + p2.y - p3.y;
    float den = dx1 * dy2 - dx2 * dy1;
    float g = (sx * dy2 - dx2 * sy) / den, h = (dx1 * sy - sx * dy1) / den;
    vec3 r0 = vec3(p1.x - p0.x + g * p1.x, p3.x - p0.x + h * p3.x, p0.x);
    vec3 r1 = vec3(p1.y - p0.y + g * p1.y, p3.y - p0.y + h * p3.y, p0.y);
    vec3 r2 = vec3(g, h, 1.0);
    vec3 c0 = cross(r1, r2), c1 = cross(r2, r0), c2 = cross(r0, r1);
    float det = dot(r0, c0);
    // inverse = [c0 c1 c2] / det (columns); store its rows
    i0 = vec3(c0.x, c1.x, c2.x) / det; i1 = vec3(c0.y, c1.y, c2.y) / det; i2 = vec3(c0.z, c1.z, c2.z) / det;
}
float segDist(vec2 p, vec2 a, vec2 b) { vec2 ab = b - a; return length(p - a - ab * clamp(dot(p - a, ab) / dot(ab, ab), 0.0, 1.0)); }
vec3 gridUV(vec3 i0, vec3 i1, vec3 i2, vec2 p) { vec3 v = vec3(p, 1.0); return vec3(dot(i0, v), dot(i1, v), dot(i2, v)); }

vec3 grid(vec2 p, vec4 view, float t, float pw, out float lampHit, out vec3 bloom) {
    lampHit = 0.0; bloom = vec3(0.0);
    if (t > 5.05) return vec3(0.0);
    float mg = 0.08;
    float l = view.x - mg, r = view.z + mg, b = view.y - mg, tp = view.w + mg;
    vec2 cells = floor(vec2(r - l, tp - b) / 0.115 + 0.5);
    vec2 fin0 = vec2(l, b), fin1 = vec2(r, b), fin2 = vec2(r, tp), fin3 = vec2(l, tp);
    // corners are dragged into place one by one: TL, TR, BR, BL
    float u3 = clamp((t - 0.55) / 0.62, 0.0, 1.0), u2 = clamp((t - 1.15) / 0.62, 0.0, 1.0);
    float u1 = clamp((t - 1.75) / 0.62, 0.0, 1.0), u0 = clamp((t - 2.35) / 0.62, 0.0, 1.0);
    vec2 c0 = mix(vec2(-0.10, -0.08), fin0, settleBase(u0) + 0.06 * settleBump(u0));
    vec2 c1 = mix(vec2(0.12, -0.05), fin1, settleBase(u1) + 0.06 * settleBump(u1));
    vec2 c2 = mix(vec2(0.09, 0.13), fin2, settleBase(u2) + 0.06 * settleBump(u2));
    vec2 c3 = mix(vec2(-0.13, 0.09), fin3, settleBase(u3) + 0.06 * settleBump(u3));
    float appear = easeIO(clamp((t - 0.15) / 0.3, 0.0, 1.0));
    float on = appear * (1.0 - clamp((t - 4.95) / 0.1, 0.0, 1.0));
    if (on <= 0.001) return vec3(0.0);
    vec3 i0, i1, i2;
    gridInverse(c0, c1, c2, c3, i0, i1, i2);
    // highlight: blinks on a cell, slides onto the lamp, flashes
    vec2 lampCell = vec2((LAMP_POS.x - l) / (r - l) * cells.x, (LAMP_POS.y + 0.012 - b) / (tp - b) * cells.y);
    vec2 lampSize = vec2((SHADE_HALF.x * 2.0 + 0.05) / (r - l) * cells.x, (SHADE_HALF.y * 2.0 + 0.11) / (tp - b) * cells.y);
    vec2 hiPos = floor(cells * vec2(0.62, 0.6)) + 0.5;
    float hiA = 0.0, hiFlash = 0.0;
    if (t > 3.1 && t < 3.75) hiA = sin((t - 3.1) / 0.65 * PI * 3.0) > 0.0 ? 1.0 : 0.2;
    float mv = easeIO(clamp((t - 3.75) / 0.4, 0.0, 1.0));
    vec2 hiSize = mix(vec2(1.0), lampSize, mv);
    if (t >= 3.75) { hiPos = mix(hiPos, lampCell, mv); hiA = 1.0; }
    if (t > 4.15) hiFlash = max(0.0, sin(PI * clamp((t - 4.15) / 0.25, 0.0, 1.0)));
    float fade = easeIO(clamp((t - 4.35) / 0.6, 0.0, 1.0));
    lampHit = (hiFlash + 0.25 * mv * (1.0 - fade)) * on;

    vec3 h = gridUV(i0, i1, i2, p);
    vec2 uv = h.xy / h.z;
    bool inside = h.z > 0.0 && uv.x >= 0.0 && uv.y >= 0.0 && uv.x <= 1.0 && uv.y <= 1.0;
    if (!inside) {   // bloom: the quad's edge spills a little light
        float de = min(min(segDist(p, c0, c1), segDist(p, c1, c2)), min(segDist(p, c2, c3), segDist(p, c3, c0))) / pw;
        bloom = C_MINT * 0.25 * exp(-de / 12.0) * on * (1.0 - fade);
        return vec3(0.0);
    }
    vec2 q = uv * cells;
    vec3 hx = gridUV(i0, i1, i2, p + vec2(pw, 0.0)), hy = gridUV(i0, i1, i2, p + vec2(0.0, pw));
    vec2 qx = hx.xy / hx.z * cells, qy = hy.xy / hy.z * cells;
    vec2 fw = max(abs(qx - q) + abs(qy - q), vec2(1e-5));
    // bloom: the highlight and the grid's edge spill past the quad
    vec2 hd = abs(q - hiPos) - 0.5 * hiSize;
    float hiOut = max(max(hd.x / fw.x, hd.y / fw.y), 0.0);   // pixels outside the highlight
    bloom = C_MINT * (0.3 * hiA + 0.55 * hiFlash) * (0.6 * exp(-hiOut / 6.0) + 0.3 * exp(-hiOut / 30.0)) * on * (1.0 - fade * 0.5);
    vec2 dl = abs(fract(q - 0.5) - 0.5) / fw;
    vec2 de = min(q, cells - q) / fw;
    float pxd = min(min(dl.x, dl.y), min(de.x, de.y));
    float lineA = clamp(1.25 - pxd, 0.0, 1.0);
    float rr = max(abs(uv.x - 0.5), abs(uv.y - 0.5)) * 2.0;
    vec2 uvx = hx.xy / hx.z, uvy = hy.xy / hy.z;
    float fr = abs(max(abs(uvx.x - 0.5), abs(uvx.y - 0.5)) * 2.0 - rr) + abs(max(abs(uvy.x - 0.5), abs(uvy.y - 0.5)) * 2.0 - rr) + 1e-5;
    float edgeT = 1.02 - 1.06 * fade;
    float mask = 1.0 - smoothstep(edgeT - fr, edgeT + fr, rr);
    float inHi = 1.0 - smoothstep(-fw.x, fw.x, max(hd.x, hd.y));
    vec3 col = (C_MINT_FILL * 0.4 + C_MINT * lineA * 0.85 + C_MINT * inHi * (0.3 * hiA + 0.55 * hiFlash)) * mask * on;
    // bloom: the lines and the highlight spill a soft mint glow
    bloom += C_MINT * (exp(-pxd / 4.0) * 0.2 + 0.07) * mask * on;
    return col;
}

// ---------------------------------------------------------------- wall
vec3 wallLight(vec2 p, vec3 hc, float I, float tint, float above) {
    vec2 d = p - LAMP_POS.xy;
    float w = 0.06 + 0.5 * abs(d.y);
    float cone = (1.0 - smoothstep(0.35 * w, w, abs(d.x))) * exp(-abs(d.y) / 0.5);
    float pool = exp(-dot(d, d) / 0.05);
    float wash = exp(-length(d) / 0.8) + above * 0.7 * exp(-abs(d.x) / 1.0);
    return hc * I * (0.9 * cone + 0.7 * pool + tint * 0.55 * wash);
}
vec3 wall(vec2 p, vec4 view, float pw, vec3 hc, float I, float tint, bool lightOnly) {
    // projector: just the light the lamp throws, on an even wall (no painted molding or ceiling)
    if (lightOnly) { vec3 light = wallLight(p, hc, I, tint, 0.0); return light * (mix(C_WALL_BOT, C_WALL_TOP, 0.5) * 2.2 + 0.32); }
    float vy = clamp((p.y - view.y) / (view.w - view.y), 0.0, 1.0);
    vec3 c = mix(C_WALL_BOT, C_WALL_TOP, smoothstep(0.0, 1.0, vy));
    float my = view.w - 0.075 * (view.w - view.y);
    float above = smoothstep(my - pw, my + pw, p.y);
    c *= mix(1.0, 1.07, above);
    float edgeL = 1.0 - smoothstep(0.0, 1.5 * pw, abs(p.y - my));
    float shadowLine = 1.0 - smoothstep(0.0, 1.5 * pw, abs(p.y - (my - 0.010)));
    float underShade = exp(-max(0.0, my - p.y) / 0.03) * (1.0 - above);
    c *= 1.0 - 0.05 * underShade - 0.10 * shadowLine;
    c += c * 0.10 * edgeL;
    vec2 cc = (p - 0.5 * (view.xy + view.zw)) / (0.5 * (view.zw - view.xy));
    c *= 1.0 - 0.10 * smoothstep(0.5, 1.5, length(cc));
    vec3 light = wallLight(p, hc, I, tint, above);
    return c + c * light * 2.2 + light * 0.32;
}

// ---------------------------------------------------------------- the arm's skin
// iq's ray / rounded cone intersection, cone from pa to pb
float iRoundCone(vec3 ro, vec3 rd, vec3 pa, vec3 pb, float ra, float rb, out vec3 n) {
    vec3 ba = pb - pa, oa = ro - pa, ob = ro - pb;
    float rr = ra - rb;
    float m0 = dot(ba, ba), m1 = dot(ba, oa), m2 = dot(ba, rd), m3 = dot(rd, oa), m5 = dot(oa, oa), m6 = dot(ob, rd), m7 = dot(ob, ob);
    float d2 = m0 - rr * rr;
    float k2 = d2 - m2 * m2, k1 = d2 * m3 - m1 * m2 + m2 * rr * ra, k0 = d2 * m5 - m1 * m1 + m1 * rr * ra * 2.0 - m0 * ra * ra;
    float h = k1 * k1 - k0 * k2;
    n = vec3(0.0, 0.0, 1.0);
    if (h < 0.0) return -1.0;
    float t = (-sqrt(h) - k1) / k2;
    float y = m1 - ra * rr + t * m2;
    if (y > 0.0 && y < d2) { n = normalize(d2 * (oa + t * rd) - ba * y); return t; }
    float h1 = m3 * m3 - m5 + ra * ra, h2 = m6 * m6 - m7 + rb * rb;
    if (max(h1, h2) < 0.0) return -1.0;
    float r = 1e20;
    if (h1 > 0.0) { r = -m3 - sqrt(h1); n = (oa + r * rd) / ra; }
    if (h2 > 0.0) { t = -m6 - sqrt(h2); if (t < r) { r = t; n = (ob + t * rd) / rb; } }
    return r;
}

// One face of the hologram skin: fill, lattice lines and dots, fresnel rim,
// fingertips picking up the lamp and the orb. fwk = lattice units per world meter (x, y).
vec4 holo(vec3 W, vec3 n, vec2 lat, vec2 fwk, float tip, float fade, float front, float tw, out vec3 bloom) {
    float ndv = abs(dot(n, gRd));
    float fp = 1.2 * tw * gPxA / max(ndv, 0.2);
    vec2 fw = fp * fwk / CELL;
    vec2 g = lat / CELL;
    vec2 dl = abs(fract(g - 0.5) - 0.5) / max(fw, vec2(1e-6));
    float density = smoothstep(2.5, 5.0, 1.0 / max(fw.x, fw.y));
    float lineA = clamp(1.0 - min(dl.x, dl.y) + 0.35, 0.0, 1.0) * density;
    float dotA = clamp(1.9 - length(dl), 0.0, 1.0) * density;
    float fres = pow(1.0 - clamp(ndv, 0.0, 1.0), 3.5);
    vec3 rimCol = mix(C_RIM, C_HOT, clamp(tip * (0.4 + fres), 0.0, 1.0));
    float nearLamp = exp(-length(W.xy - LAMP_POS.xy) / 0.09);
    float nearOrb = exp(-length(W - gOrbPos) / 0.07);
    vec3 pick = gLampCol * nearLamp * 1.6 + gOrbCol * nearOrb * 1.2;
    rimCol = mix(rimCol, rimCol * 0.35 + pick, clamp(length(pick), 0.0, 0.85));
    float scan = 1.0 + 0.025 * smoothstep(0.6, 1.0, sin(lat.y * 22.0 - gT * 1.6));
    float face = mix(0.35, 1.0, front);
    fade *= clamp(W.z / (tw * gPxA) + 0.5, 0.0, 1.0);   // the wall hides whatever swings behind it
    vec3 lit = (C_LINE * lineA * 0.85 + C_HOT * dotA * 0.5 + rimCol * fres * 1.05 + pick * tip * 0.5) * face;
    bloom = (C_LINE * lineA * 0.05 + C_HOT * dotA * 0.08 + rimCol * fres * fres * 0.5 + pick * tip * 0.35) * front * scan * fade;
    return vec4((C_FILL * 0.55 * face + lit) * scan * fade, 0.55 * face * fade);
}
void addSurface(vec4 c, vec3 bloom, float tw, float front) {
    gFigAll += c.rgb; gTrans *= 1.0 - c.a;
    if (front > 0.5) { gFigBloom += bloom; if (tw < gBestT && c.a > 0.01) { gBestT = tw; gFigBest = c.rgb; } }
}
// halo hugging a capsule's silhouette; returns false when the ray misses it
bool capHalo(vec3 A, vec3 B, float rA, float rB, vec3 col) {
    vec3 ab = B - A, ao = gRo - A;
    float bb = dot(ab, ab), br = dot(ab, gRd), ba = dot(ab, ao), ra = dot(gRd, ao);
    float s = clamp((ba - br * ra) / max(bb - br * br, 1e-9), 0.0, 1.0);
    vec3 cp = A + ab * s;
    float tc = dot(cp - gRo, gRd);
    float dist = length(gRo + gRd * tc - cp);
    float rs = mix(rA, rB, s);
    float hz = max(tc, 0.05) * gPxA;
    gGlow = max(gGlow, col * smoothstep(-0.01, 0.01, cp.z) * (GLOW_NEAR * exp(-abs(dist - rs) / (4.0 * hz)) + GLOW_FAR * exp(-max(dist - rs, 0.0) / (30.0 * hz))));
    return dist <= max(rA, rB) * 1.02;
}

void capData(int k, out vec4 r, out vec3 restP, out vec3 pa, out vec3 ax, out vec3 b1, out vec3 m) {
    pa = vec3(0.0); ax = vec3(0.0, 0.0, 1.0); b1 = vec3(1.0, 0.0, 0.0); m = vec3(0.0);
    if (k < 9) {
        if (k < 4) {
            if (k < 2) {
                if (k < 1) {
                    r = vec4(0.0352, 0.0253, 0.0, 0.0); restP = vec3(0.03913, 0.05578, 0.00916);
                } else {
                    r = vec4(0.0352, 0.0253, 0.0, 0.0); restP = vec3(0.03913, 0.05578, 0.00916);
                }
            } else {
                if (k < 3) {
                    r = vec4(0.0352, 0.0253, 0.0, 0.0); restP = vec3(0.03913, 0.05578, 0.00916);
                } else {
                    r = vec4(0.0352, 0.0253, 0.0, 0.0); restP = vec3(0.03913, 0.05578, 0.00916);
                }
            }
        } else {
            if (k < 6) {
                if (k < 5) {
                    r = vec4(0.0275, 0.0231, 0.0, 0.0); restP = vec3(0.01997, 0.01982, -0.01887);
                } else {
                    r = vec4(0.0231, 0.0209, 1.0, 0.06875); restP = vec3(0.00482, -0.00344, -0.0358);
                    pa = vec3(0.00482, -0.00344, -0.0358); ax = vec3(-0.2783978, -0.7073847, -0.6496934); b1 = vec3(-0.9544741, 0.2792009, 0.1050049); m = vec3(0.0609888, 0.0338006, 0.0);
                }
            } else {
                if (k < 7) {
                    r = vec4(0.0209, 0.0187, 1.0, 0.06875); restP = vec3(-0.00459, -0.02735, -0.05776);
                    pa = vec3(-0.00459, -0.02735, -0.05776); ax = vec3(-0.3500252, -0.5755222, -0.7390917); b1 = vec3(-0.9316689, 0.2958764, 0.210832); m = vec3(0.0947894, 0.0148561, 1.0);
                } else {
                    if (k < 8) {
                        r = vec4(0.02156, 0.01936, 1.0, 0.05625); restP = vec3(0.03181, -0.03277, -0.01439);
                        pa = vec3(0.03181, -0.03277, -0.01439); ax = vec3(-0.0712222, -0.9968898, 0.0337368); b1 = vec3(-0.9908945, 0.0668384, -0.1168794); m = vec3(0.0891478, 0.0453511, 0.0);
                    } else {
                        r = vec4(0.01936, 0.0176, 1.0, 0.05625); restP = vec3(0.02858, -0.07798, -0.01286);
                        pa = vec3(0.02858, -0.07798, -0.01286); ax = vec3(-0.0913648, -0.994312, 0.0547365); b1 = vec3(-0.9893364, 0.0843719, -0.1187221); m = vec3(0.1344989, 0.0242982, 0.0);
                    }
                }
            }
        }
    } else {
        if (k < 14) {
            if (k < 11) {
                if (k < 10) {
                    r = vec4(0.0176, 0.01584, 1.0, 0.05625); restP = vec3(0.02636, -0.10214, -0.01153);
                    pa = vec3(0.02636, -0.10214, -0.01153); ax = vec3(0.0526548, -0.9926723, 0.1087624); b1 = vec3(-0.9923168, -0.0642223, -0.105749); m = vec3(0.1587971, 0.0115849, 1.0);
                } else {
                    r = vec4(0.02244, 0.02024, 1.0, 0.05625); restP = vec3(0.03658, -0.03587, 0.00743);
                    pa = vec3(0.03658, -0.03587, 0.00743); ax = vec3(-0.0955437, -0.9910528, 0.0931978); b1 = vec3(-0.9891154, 0.0839959, -0.1208113); m = vec3(0.0917018, 0.0468895, 0.0);
                }
            } else {
                if (k < 12) {
                    r = vec4(0.02024, 0.01826, 1.0, 0.05625); restP = vec3(0.0321, -0.08234, 0.0118);
                    pa = vec3(0.0321, -0.08234, 0.0118); ax = vec3(-0.1005491, -0.9887936, 0.1103499); b1 = vec3(-0.9887042, 0.0869128, -0.1221071); m = vec3(0.1385913, 0.0275487, 0.0);
                } else {
                    if (k < 13) {
                        r = vec4(0.01826, 0.01628, 1.0, 0.05625); restP = vec3(0.02933, -0.10958, 0.01484);
                        pa = vec3(0.02933, -0.10958, 0.01484); ax = vec3(0.0844127, -0.9875464, 0.1327656); b1 = vec3(-0.9902208, -0.0979911, -0.0992999); m = vec3(0.16614, 0.012202, 1.0);
                    } else {
                        r = vec4(0.02112, 0.01892, 1.0, 0.05625); restP = vec3(0.0326, -0.02892, 0.02662);
                        pa = vec3(0.0326, -0.02892, 0.02662); ax = vec3(-0.1017059, -0.9708719, 0.2169416); b1 = vec3(-0.9891691, 0.0754955, -0.1258769); m = vec3(0.0845044, 0.0428687, 0.0);
                    }
                }
            }
        } else {
            if (k < 16) {
                if (k < 15) {
                    r = vec4(0.01892, 0.01716, 1.0, 0.05625); restP = vec3(0.02824, -0.07054, 0.03592);
                    pa = vec3(0.02824, -0.07054, 0.03592); ax = vec3(-0.1234507, -0.9623886, 0.2420085); b1 = vec3(-0.9868633, 0.0934501, -0.1317872); m = vec3(0.127373, 0.0265693, 0.0);
                } else {
                    r = vec4(0.01716, 0.0154, 1.0, 0.05625); restP = vec3(0.02496, -0.09611, 0.04235);
                    pa = vec3(0.02496, -0.09611, 0.04235); ax = vec3(-0.0652969, -0.9786166, 0.1950536); b1 = vec3(-0.9920832, 0.0426528, -0.1181173); m = vec3(0.1539424, 0.0119454, 1.0);
                }
            } else {
                if (k < 17) {
                    r = vec4(0.0187, 0.01672, 1.0, 0.05); restP = vec3(0.02543, -0.01812, 0.04421);
                    pa = vec3(0.02543, -0.01812, 0.04421); ax = vec3(-0.1278133, -0.9690102, 0.2113836); b1 = vec3(-0.9861225, 0.1013933, -0.1314601); m = vec3(0.073578, 0.0345817, 0.0);
                } else {
                    if (k < 18) {
                        r = vec4(0.01672, 0.0154, 1.0, 0.05); restP = vec3(0.02101, -0.05163, 0.05152);
                        pa = vec3(0.02101, -0.05163, 0.05152); ax = vec3(-0.1407983, -0.9353732, 0.3244268); b1 = vec3(-0.9851078, 0.0996985, -0.1400816); m = vec3(0.1081597, 0.0203127, 0.0);
                    } else {
                        r = vec4(0.0154, 0.01386, 1.0, 0.05); restP = vec3(0.01815, -0.07063, 0.05811);
                        pa = vec3(0.01815, -0.07063, 0.05811); ax = vec3(-0.0663527, -0.9658939, 0.2502925); b1 = vec3(-0.9923458, 0.0376972, -0.1175957); m = vec3(0.1284724, 0.0119061, 1.0);
                    }
                }
            }
        }
    }
}

// lattice, tip glow and fade at a hit on hand capsule k (rest-pose hand meters, as latticeFor)
void handAttr(vec3 hit, vec3 jp, vec4 qr, vec3 restP, vec4 r, vec3 pa, vec3 ax, vec3 b1, vec3 m,
              out vec2 lat, out vec2 fwk, out float tip, out float fade) {
    vec3 xa = RIG_W + qrot(qconj(gQroot), hit - gW) / HS;   // armature space
    vec3 x = restP + qrot(qr, xa - jp);                      // rest pose
    fade = 1.0;
    if (r.z < 0.5) {                                         // palm: a flat lattice across and along the hand
        vec3 v = x - RIG_W;
        lat = vec2(dot(v, RIG_L), dot(v, RIG_F));
        fwk = vec2(1.0 / HS);
        float k = clamp((dot(v, RIG_F) + 0.015) / 0.025, 0.0, 1.0);
        fade = k * k * (3.0 - 2.0 * k);                      // the open wrist edge dissolves into the forearm
        float kn = min(min(length(x - KN0), length(x - KN1)), min(length(x - KN2), length(x - KN3)));
        tip = 0.55 * exp(-kn / 0.008);
        return;
    }
    float t = dot(x - pa, ax);
    vec3 rv = x - pa - ax * t;
    vec3 b2 = cross(ax, b1);
    float ang = atan(dot(rv, b2), dot(rv, b1));
    lat = vec2(ang / TAU * r.w, m.x + t);
    fwk = vec2(r.w / (TAU * max(length(rv), 1e-4)), 1.0) / HS;
    tip = m.z > 0.5 ? min(1.0, 0.35 + 0.65 * max(0.0, t / m.y)) : 0.55 * exp(-abs(t) / 0.007);
}

void handCapsule(vec3 A, vec3 B, vec3 jp, vec4 qr, int k) {
    vec4 r; vec3 restP, pa, ax, b1, m;
    capData(k, r, restP, pa, ax, b1, m);
    if (!capHalo(A, B, r.x, r.y, C_RIM)) return;
    vec3 n, bloom;
    float tf = iRoundCone(gRo, gRd, A, B, r.x, r.y, n);
    if (tf <= 0.0) return;
    vec2 lat, fwk; float tip, fade;
    vec3 hit = gRo + gRd * tf;
    handAttr(hit, jp, qr, restP, r, pa, ax, b1, m, lat, fwk, tip, fade);
    addSurface(holo(hit, n, lat, fwk, tip, fade, 1.0, tf, bloom), bloom, tf, 1.0);
    float Tf = dot(0.5 * (A + B) - gRo, gRd) + length(B - A) + max(r.x, r.y);
    float tr = iRoundCone(gRo + gRd * Tf, -gRd, A, B, r.x, r.y, n);
    if (tr <= 0.0) return;
    float tb = Tf - tr;
    hit = gRo + gRd * tb;
    handAttr(hit, jp, qr, restP, r, pa, ax, b1, m, lat, fwk, tip, fade);
    addSurface(holo(hit, n, lat, fwk, tip, fade, 0.0, tb, bloom), bloom, tb, 0.0);
}

// forearm / upper arm: an open tapered tube, lattice running along it from the elbow
void tube(vec3 A, vec3 B, float rA, float rB, vec3 ref, float v0, float fadeA, float fadeB) {
    if (!capHalo(A, B, rA, rB, C_RIM)) return;
    vec3 axis = B - A;
    float len = length(axis);
    axis /= len;
    vec3 U = normalize(ref - axis * dot(ref, axis)), Vv = cross(axis, U);
    float around = max(12.0, floor(TAU * ((rA + rB) * 0.5 / HS) / CELL + 0.5)) * CELL;
    vec3 n, bloom;
    for (int face = 0; face < 2; face++) {
        float th;
        if (face == 0) th = iRoundCone(gRo, gRd, A, B, rA, rB, n);
        else {
            float Tf = dot(0.5 * (A + B) - gRo, gRd) + len + max(rA, rB);
            float tr = iRoundCone(gRo + gRd * Tf, -gRd, A, B, rA, rB, n);
            th = tr > 0.0 ? Tf - tr : -1.0;
        }
        if (th <= 0.0) continue;
        vec3 hit = gRo + gRd * th, q = hit - A;
        float s = dot(q, axis) / len;
        vec3 rv = q - axis * dot(q, axis);
        float ang = atan(dot(rv, Vv), dot(rv, U)); if (ang < 0.0) ang += TAU;
        float fa = fadeA > 0.0 ? min(1.0, s * len / fadeA) : 1.0, fb = fadeB > 0.0 ? min(1.0, (1.0 - s) * len / fadeB) : 1.0;
        float fd = clamp(min(fa, fb), 0.0, 1.0);
        fd = fd * fd * (3.0 - 2.0 * fd) * step(0.0, s) * step(s, 1.0);   // open ends: no caps
        vec2 lat = vec2(ang / TAU * around, v0 + s * len / HS);
        vec2 fwk = vec2(around / (TAU * max(length(rv), 1e-4)), 1.0 / HS);
        float front = face == 0 ? 1.0 : 0.0;
        addSurface(holo(hit, n, lat, fwk, 0.0, fd, front, th, bloom), bloom, th, front);
    }
}

// the elbow: a joint sphere with a quiet fill, so any bend keeps a smooth silhouette
void elbowSphere(vec3 C, float rad) {
    vec3 oc = gRo - C;
    float b = dot(oc, gRd), c = dot(oc, oc) - rad * rad, h = b * b - c;
    if (h < 0.0) return;
    h = sqrt(h);
    float ae = floor(TAU * 0.041 / CELL + 0.5) * CELL;
    vec3 bloom;
    for (int face = 0; face < 2; face++) {
        float th = face == 0 ? -b - h : -b + h;
        if (th <= 0.0) continue;
        vec3 hit = gRo + gRd * th, n = (hit - C) / rad;
        float theta = acos(clamp(n.y, -1.0, 1.0)), phi = atan(n.z, -n.x); if (phi < 0.0) phi += TAU;
        vec2 lat = vec2(phi / TAU * ae, (1.0 - theta / PI) * ae * 0.5);
        vec2 fwk = vec2(ae / (TAU * rad * max(sin(theta), 0.05)), ae * 0.5 / (PI * rad));
        float front = face == 0 ? 1.0 : 0.0;
        addSurface(holo(hit, n, lat, fwk, 0.0, 0.4, front, th, bloom), bloom, th, front);
    }
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
vec3 toSRGB(vec3 c) { return mix(pow(c, vec3(1.0 / 2.4)) * 1.055 - 0.055, c * 12.92, step(c, vec3(0.0031308))); }

void main() {
    float t = mod(TIME * speed + timeOffset, LOOP);
    gT = t;

    // camera: fits x -0.55..0.65, y -0.42..0.42 on the wall (z = 0), fov 30 degrees
    float aspect = RENDERSIZE.x / RENDERSIZE.y;
    float halfH = max(0.42, 0.6 / aspect), halfW = halfH * aspect;
    float D = halfH / 0.26794919;
    vec2 cxy = vec2(0.05, 0.0);
    vec2 p = cxy + (isf_FragNormCoord * 2.0 - 1.0) * vec2(halfW, halfH);
    gRo = vec3(cxy, D);
    gRd = normalize(vec3(p, 0.0) - gRo);
    float pw = 2.0 * halfH / RENDERSIZE.y;
    gPxA = pw / D;
    vec4 view = vec4(cxy - vec2(halfW, halfH), cxy + vec2(halfW, halfH));
    bool lightOnly = projector;

    // lamp, grid, orb
    vec3 core, haloC; float I, tint, flame;
    lampState(t, core, haloC, I, tint, flame);
    gLampCol = core * min(1.2, I);
    float lampHit;
    vec3 gridBloom;
    vec3 gridCol = grid(p, view, t, pw, lampHit, gridBloom);
    vec3 orbP; vec2 orbSc; float orbOn;
    orbState(t, orbP, orbSc, orbOn);
    gOrbPos = orbP; gOrbCol = C_ORB_EDGE * orbOn;

    // background: wall (or just the lamp's light on it), grid, lamp halo, the sconce
    vec3 bg = wall(p, view, pw, haloC, I, tint, lightOnly) + gridCol;
    vec2 lh = (p - LAMP_POS.xy) / 0.45;   // the halo card, behind the sconce
    float lr = length(lh);
    vec3 halo2 = haloC * I * 0.55 * exp(-lr * lr * 5.0) * (1.0 - smoothstep(0.8, 1.0, lr));
    vec3 lampBloom;
    vec2 lp = (gRo + gRd * ((0.035 - gRo.z) / gRd.z)).xy - (LAMP_POS.xy + vec2(0.0, 0.045));
    vec4 lamp = lampShade(lp, pw, core, I, flame, lampHit, lightOnly, lampBloom);
    bg = (bg + halo2) * (1.0 - lamp.a) + lamp.rgb;

    // the arm
    vec3 Sv, E, W; vec4 Qk;
    evalArm(t, Sv, E, W, Qk);
    gFigAll = vec3(0.0); gFigBest = vec3(0.0); gBestT = 1e9; gTrans = 1.0; gFigBloom = vec3(0.0); gGlow = vec3(0.0);
    float rW = 0.0528, rE = 0.0902, rS = 0.11;
    vec3 Fh = qrot(Qk, vec3(0.0, 0.0, 1.0)), Lh = qrot(Qk, vec3(1.0, 0.0, 0.0));
    vec3 wristEnd = W + Fh * 0.0264;
    vec3 outDir = normalize(Sv - E);
    float fLen = length(E - wristEnd) / HS;
    tube(wristEnd, E, rW, rE, Lh, -0.1, 0.077, rE * 0.35);
    tube(E, E + outDir * max(length(Sv - E), 3.0), rE, rS, vec3(0.0, 0.0, 1.0), -0.1 + fLen, rE * 0.35, 0.0);
    elbowSphere(E, rE * 0.97);
    vec3 cA[19], cB[19], cJP[19];
    vec4 cQR[19];
    cA[0] = toWorld(jp_wrist + qrot(jq_wrist, vec3(-0.004239, -0.0013176, -0.015939))); cB[0] = toWorld(jp_wrist + qrot(jq_wrist, vec3(-0.0234745, -0.0072088, -0.0845523)));
    cJP[0] = jp_wrist; cQR[0] = qmul(vec4(-0.5, -0.5, -0.5, 0.5), vec4(-jq_wrist.xyz, jq_wrist.w));
    cA[1] = toWorld(jp_wrist + qrot(jq_wrist, vec3(-0.0003114, -0.000459, -0.016497))); cB[1] = toWorld(jp_wrist + qrot(jq_wrist, vec3(-0.0016545, -0.0024388, -0.0876523)));
    cJP[1] = jp_wrist; cQR[1] = qmul(vec4(-0.5, -0.5, -0.5, 0.5), vec4(-jq_wrist.xyz, jq_wrist.w));
    cA[2] = toWorld(jp_wrist + qrot(jq_wrist, vec3(0.0031428, -0.0011754, -0.015246))); cB[2] = toWorld(jp_wrist + qrot(jq_wrist, vec3(0.0175355, -0.0064188, -0.0807023)));
    cJP[2] = jp_wrist; cQR[2] = qmul(vec4(-0.5, -0.5, -0.5, 0.5), vec4(-jq_wrist.xyz, jq_wrist.w));
    cA[3] = toWorld(jp_wrist + qrot(jq_wrist, vec3(0.006309, -0.002466, -0.013302))); cB[3] = toWorld(jp_wrist + qrot(jq_wrist, vec3(0.0351255, -0.0135888, -0.0699023)));
    cJP[3] = jp_wrist; cQR[3] = qmul(vec4(-0.5, -0.5, -0.5, 0.5), vec4(-jq_wrist.xyz, jq_wrist.w));
    cA[4] = toWorld(jp_thumb_metacarpal + qrot(jq_thumb_metacarpal, vec3(0.0, 0.0, 0.0))); cB[4] = toWorld(jp_thumb_metacarpal + qrot(jq_thumb_metacarpal, vec3(-0.0001645, 0.0000906, -0.0325137)));
    cJP[4] = jp_thumb_metacarpal; cQR[4] = qmul(vec4(-0.4643306, 0.1582602, -0.2093703, 0.8458811), vec4(-jq_thumb_metacarpal.xyz, jq_thumb_metacarpal.w));
    cA[5] = toWorld(jp_thumb_proximal + qrot(jq_thumb_proximal, vec3(0.0, 0.0, 0.0))); cB[5] = toWorld(jp_thumb_proximal + qrot(jq_thumb_proximal, vec3(-0.000148, 0.0000637, -0.0338002)));
    cJP[5] = jp_thumb_proximal; cQR[5] = qmul(vec4(-0.4182693, 0.0351899, -0.2559396, 0.8708085), vec4(-jq_thumb_proximal.xyz, jq_thumb_proximal.w));
    cA[6] = toWorld(jp_thumb_distal + qrot(jq_thumb_distal, vec3(0.0, 0.0, 0.0))); cB[6] = toWorld(jp_thumb_distal + qrot(jq_thumb_distal, vec3(-0.0001522, 0.0000297, -0.0148553)));
    cJP[6] = jp_thumb_distal; cQR[6] = qmul(vec4(-0.3449701, 0.10875, -0.2085801, 0.9086602), vec4(-jq_thumb_distal.xyz, jq_thumb_distal.w));
    cA[7] = toWorld(jp_index_proximal + qrot(jq_index_proximal, vec3(0.0, 0.0, 0.0))); cB[7] = toWorld(jp_index_proximal + qrot(jq_index_proximal, vec3(0.0007396, 0.0009745, -0.0453345)));
    cJP[7] = jp_index_proximal; cQR[7] = qmul(vec4(-0.5425785, -0.4636987, -0.5029286, 0.4874986), vec4(-jq_index_proximal.xyz, jq_index_proximal.w));
    cA[8] = toWorld(jp_index_intermediate + qrot(jq_index_intermediate, vec3(0.0, 0.0, 0.0))); cB[8] = toWorld(jp_index_intermediate + qrot(jq_index_intermediate, vec3(0.000077, 0.0000211, -0.0242981)));
    cJP[8] = jp_index_intermediate; cQR[8] = qmul(vec4(-0.5372702, -0.4869602, -0.5076202, 0.4653302), vec4(-jq_index_intermediate.xyz, jq_index_intermediate.w));
    cA[9] = toWorld(jp_index_distal + qrot(jq_index_distal, vec3(0.0, 0.0, 0.0))); cB[9] = toWorld(jp_index_distal + qrot(jq_index_distal, vec3(0.0000619, -0.0000173, -0.0115847)));
    cJP[9] = jp_index_distal; cQR[9] = qmul(vec4(-0.5075981, -0.542288, -0.4631283, 0.4835082), vec4(-jq_index_distal.xyz, jq_index_distal.w));
    cA[10] = toWorld(jp_middle_proximal + qrot(jq_middle_proximal, vec3(0.0, 0.0, 0.0))); cB[10] = toWorld(jp_middle_proximal + qrot(jq_middle_proximal, vec3(-0.0002157, 0.0005242, -0.0468861)));
    cJP[10] = jp_middle_proximal; cQR[10] = qmul(vec4(-0.5452108, -0.5015008, -0.5051608, 0.4427807), vec4(-jq_middle_proximal.xyz, jq_middle_proximal.w));
    cA[11] = toWorld(jp_middle_intermediate + qrot(jq_middle_intermediate, vec3(0.0, 0.0, 0.0))); cB[11] = toWorld(jp_middle_intermediate + qrot(jq_middle_intermediate, vec3(-0.000762, -0.0001154, -0.0275379)));
    cJP[11] = jp_middle_intermediate; cQR[11] = qmul(vec4(-0.5477411, -0.518611, -0.496011, 0.4301109), vec4(-jq_middle_intermediate.xyz, jq_middle_intermediate.w));
    cA[12] = toWorld(jp_middle_distal + qrot(jq_middle_distal, vec3(0.0, 0.0, 0.0))); cB[12] = toWorld(jp_middle_distal + qrot(jq_middle_distal, vec3(-0.0000664, -0.0000479, -0.0122017)));
    cJP[12] = jp_middle_distal; cQR[12] = qmul(vec4(-0.4841509, -0.5786511, -0.4640709, 0.4641109), vec4(-jq_middle_distal.xyz, jq_middle_distal.w));
    cA[13] = toWorld(jp_ring_proximal + qrot(jq_ring_proximal, vec3(0.0, 0.0, 0.0))); cB[13] = toWorld(jp_ring_proximal + qrot(jq_ring_proximal, vec3(-0.0005679, 0.0005542, -0.0428613)));
    cJP[13] = jp_ring_proximal; cQR[13] = qmul(vec4(-0.5545004, -0.5537904, -0.4880703, 0.3842303), vec4(-jq_ring_proximal.xyz, jq_ring_proximal.w));
    cA[14] = toWorld(jp_ring_intermediate + qrot(jq_ring_intermediate, vec3(0.0, 0.0, 0.0))); cB[14] = toWorld(jp_ring_intermediate + qrot(jq_ring_intermediate, vec3(-0.0006606, -0.0000309, -0.0265611)));
    cJP[14] = jp_ring_intermediate; cQR[14] = qmul(vec4(-0.5459917, -0.5786318, -0.4912816, 0.3545711), vec4(-jq_ring_intermediate.xyz, jq_ring_intermediate.w));
    cA[15] = toWorld(jp_ring_distal + qrot(jq_ring_distal, vec3(0.0, 0.0, 0.0))); cB[15] = toWorld(jp_ring_distal + qrot(jq_ring_distal, vec3(0.0000637, 0.0000171, -0.0119453)));
    cJP[15] = jp_ring_distal; cQR[15] = qmul(vec4(-0.5122405, -0.5765706, -0.5031205, 0.3899304), vec4(-jq_ring_distal.xyz, jq_ring_distal.w));
    cA[16] = toWorld(jp_pinky_proximal + qrot(jq_pinky_proximal, vec3(0.0, 0.0, 0.0))); cB[16] = toWorld(jp_pinky_proximal + qrot(jq_pinky_proximal, vec3(-0.000123, 0.0004905, -0.034578)));
    cJP[16] = jp_pinky_proximal; cQR[16] = qmul(vec4(-0.5180289, -0.5807288, -0.5245189, 0.3453693), vec4(-jq_pinky_proximal.xyz, jq_pinky_proximal.w));
    cA[17] = toWorld(jp_pinky_intermediate + qrot(jq_pinky_intermediate, vec3(0.0, 0.0, 0.0))); cB[17] = toWorld(jp_pinky_intermediate + qrot(jq_pinky_intermediate, vec3(0.0001798, -0.0000465, -0.0203119)));
    cJP[17] = jp_pinky_intermediate; cQR[17] = qmul(vec4(-0.5159602, -0.6264202, -0.4996402, 0.3028901), vec4(-jq_pinky_intermediate.xyz, jq_pinky_intermediate.w));
    cA[18] = toWorld(jp_pinky_distal + qrot(jq_pinky_distal, vec3(0.0, 0.0, 0.0))); cB[18] = toWorld(jp_pinky_distal + qrot(jq_pinky_distal, vec3(0.0001183, 0.0000028, -0.0119055)));
    cJP[18] = jp_pinky_distal; cQR[18] = qmul(vec4(-0.4843809, -0.6212112, -0.5096009, 0.3460906), vec4(-jq_pinky_distal.xyz, jq_pinky_distal.w));
    for (int k = 0; k < 19; k++) handCapsule(cA[k], cB[k], cJP[k], cQR[k], k);
    vec3 fig = gFigBest + 0.45 * (gFigAll - gFigBest);
    vec3 col = bg * gTrans + fig;

    // the orb: a crisp disc with a soft halo
    if (orbOn > 0.0) {
        vec3 oh = gRo + gRd * ((orbP.z - gRo.z) / gRd.z);
        vec2 d = oh.xy - orbP.xy;
        float hr = length(d) / 0.21;
        col += C_ORB_EDGE * 0.3 * orbOn * exp(-hr * hr * 5.0) * (1.0 - smoothstep(0.8, 1.0, hr));
        vec2 q = d / (0.045 * orbSc);
        float r = length(q), aa = 1.2 * pw / (0.045 * orbSc.x);
        float disc = 1.0 - smoothstep(1.0 - aa, 1.0 + aa, r);
        vec3 c = mix(C_ORB_CORE, C_ORB_EDGE, smoothstep(0.2, 1.0, r)) + vec3(0.25) * exp(-dot(q - vec2(-0.3, 0.35), q - vec2(-0.3, 0.35)) * 14.0);
        float a = disc * orbOn;
        col = col * (1.0 - a) + c * a;
        gGlow += C_ORB_EDGE * orbOn * (0.35 * exp(-max(r - 1.0, 0.0) * 0.045 / 0.012) + 0.12 * exp(-max(r - 1.0, 0.0) * 0.045 / 0.06));
    }

    // bolts: streaks from the fingertip to the lamp
    {
        float u = (t - 14.4) / 0.18;
        if (u >= 0.0 && u <= 1.15) {
            vec3 from = vec3(0.0936776, -0.1153988, 0.3586088), to = LAMP_POS + vec3(0.0, 0.0, 0.06);
            vec3 head = mix(from, to, easeInCubic(min(1.0, u)) * 0.35 + min(1.0, u) * 0.65);
            vec3 dir = normalize(to - from);
            float trail = 0.26 * min(1.0, u * 3.0) * (u > 1.0 ? max(0.0, 1.0 - (u - 1.0) / 0.15) : 1.0);
            if (trail > 0.002) {
                vec3 mid = head - dir * trail * 0.5;
                vec3 bh = gRo + gRd * ((mid.z - gRo.z) / gRd.z);
                vec2 dd = normalize(dir.xy), rel = bh.xy - mid.xy;
                float bu = dot(rel, dd) / trail + 0.5, bv = abs(dot(rel, vec2(-dd.y, dd.x))) / 0.016;
                if (bu >= 0.0 && bu <= 1.0 && bv <= 1.0) {
                    float taper = mix(0.25, 1.0, bu), aa = pw / 0.016;
                    float coreB = 1.0 - smoothstep(0.3 * taper - aa, 0.3 * taper + aa, bv);
                    float gl = exp(-bv * bv / (0.22 * taper)) * 0.6;
                    vec3 bc = vec3(0.765337, 0.0038347, 0.0057248);
                    col += (mix(bc * 1.6, vec3(1.0, 0.97, 0.9) * 2.2, coreB * smoothstep(0.6, 1.0, bu)) * coreB + bc * gl) * pow(bu, 1.6);
                }
                float dh = length(bh.xy - head.xy);   // the hot head
                gGlow += (vec3(0.765337, 0.0038347, 0.0057248) + vec3(0.7)) * min(1.0, u * 3.0) * (0.9 * exp(-dh / 0.005) + 0.3 * exp(-dh / 0.02));
                float along = clamp(dot(rel, dd) / trail + 0.5, 0.0, 1.0);
                float dl = length(rel - dd * (along - 0.5) * trail);
                gGlow += (vec3(0.765337, 0.0038347, 0.0057248) * 1.6 + vec3(0.9) * smoothstep(0.6, 1.0, along)) * pow(along, 1.6) * (0.9 * exp(-dl / 0.005) + 0.25 * exp(-dl / 0.025));
            }
        }
    }
    {
        float u = (t - 15.9) / 0.18;
        if (u >= 0.0 && u <= 1.15) {
            vec3 from = vec3(0.094589, -0.1156832, 0.3393425), to = LAMP_POS + vec3(0.0, 0.0, 0.06);
            vec3 head = mix(from, to, easeInCubic(min(1.0, u)) * 0.35 + min(1.0, u) * 0.65);
            vec3 dir = normalize(to - from);
            float trail = 0.26 * min(1.0, u * 3.0) * (u > 1.0 ? max(0.0, 1.0 - (u - 1.0) / 0.15) : 1.0);
            if (trail > 0.002) {
                vec3 mid = head - dir * trail * 0.5;
                vec3 bh = gRo + gRd * ((mid.z - gRo.z) / gRd.z);
                vec2 dd = normalize(dir.xy), rel = bh.xy - mid.xy;
                float bu = dot(rel, dd) / trail + 0.5, bv = abs(dot(rel, vec2(-dd.y, dd.x))) / 0.016;
                if (bu >= 0.0 && bu <= 1.0 && bv <= 1.0) {
                    float taper = mix(0.25, 1.0, bu), aa = pw / 0.016;
                    float coreB = 1.0 - smoothstep(0.3 * taper - aa, 0.3 * taper + aa, bv);
                    float gl = exp(-bv * bv / (0.22 * taper)) * 0.6;
                    vec3 bc = vec3(0.0, 0.4874642, 0.0325552);
                    col += (mix(bc * 1.6, vec3(1.0, 0.97, 0.9) * 2.2, coreB * smoothstep(0.6, 1.0, bu)) * coreB + bc * gl) * pow(bu, 1.6);
                }
                float dh = length(bh.xy - head.xy);   // the hot head
                gGlow += (vec3(0.0, 0.4874642, 0.0325552) + vec3(0.7)) * min(1.0, u * 3.0) * (0.9 * exp(-dh / 0.005) + 0.3 * exp(-dh / 0.02));
                float along = clamp(dot(rel, dd) / trail + 0.5, 0.0, 1.0);
                float dl = length(rel - dd * (along - 0.5) * trail);
                gGlow += (vec3(0.0, 0.4874642, 0.0325552) * 1.6 + vec3(0.9) * smoothstep(0.6, 1.0, along)) * pow(along, 1.6) * (0.9 * exp(-dl / 0.005) + 0.25 * exp(-dl / 0.025));
            }
        }
    }
    {
        float u = (t - 17.4) / 0.18;
        if (u >= 0.0 && u <= 1.15) {
            vec3 from = vec3(0.0939406, -0.107927, 0.3602605), to = LAMP_POS + vec3(0.0, 0.0, 0.06);
            vec3 head = mix(from, to, easeInCubic(min(1.0, u)) * 0.35 + min(1.0, u) * 0.65);
            vec3 dir = normalize(to - from);
            float trail = 0.26 * min(1.0, u * 3.0) * (u > 1.0 ? max(0.0, 1.0 - (u - 1.0) / 0.15) : 1.0);
            if (trail > 0.002) {
                vec3 mid = head - dir * trail * 0.5;
                vec3 bh = gRo + gRd * ((mid.z - gRo.z) / gRd.z);
                vec2 dd = normalize(dir.xy), rel = bh.xy - mid.xy;
                float bu = dot(rel, dd) / trail + 0.5, bv = abs(dot(rel, vec2(-dd.y, dd.x))) / 0.016;
                if (bu >= 0.0 && bu <= 1.0 && bv <= 1.0) {
                    float taper = mix(0.25, 1.0, bu), aa = pw / 0.016;
                    float coreB = 1.0 - smoothstep(0.3 * taper - aa, 0.3 * taper + aa, bv);
                    float gl = exp(-bv * bv / (0.22 * taper)) * 0.6;
                    vec3 bc = vec3(1.7306356, 0.4356416, 0.1081143);
                    col += (mix(bc * 1.6, vec3(1.0, 0.97, 0.9) * 2.2, coreB * smoothstep(0.6, 1.0, bu)) * coreB + bc * gl) * pow(bu, 1.6);
                }
                float dh = length(bh.xy - head.xy);   // the hot head
                gGlow += (vec3(1.7306356, 0.4356416, 0.1081143) + vec3(0.7)) * min(1.0, u * 3.0) * (0.9 * exp(-dh / 0.005) + 0.3 * exp(-dh / 0.02));
                float along = clamp(dot(rel, dd) / trail + 0.5, 0.0, 1.0);
                float dl = length(rel - dd * (along - 0.5) * trail);
                gGlow += (vec3(1.7306356, 0.4356416, 0.1081143) * 1.6 + vec3(0.9) * smoothstep(0.6, 1.0, along)) * pow(along, 1.6) * (0.9 * exp(-dl / 0.005) + 0.25 * exp(-dl / 0.025));
            }
        }
    }

    // bloom approximations
    float lampD = sdRR(p - LAMP_POS.xy, SHADE_HALF, 0.03);
    vec3 lampGlow = (core * I * 0.3 + core * flame * 0.15) * (0.5 * exp(-max(lampD, 0.0) / 0.012) + 0.25 * exp(-max(lampD, 0.0) / 0.08));
    col += (gGlow * 0.6 + gFigBloom * 0.3 + lampGlow + gridBloom) * glow;

    gl_FragColor = vec4(toSRGB(aces(col)) * intensity, 1.0);
}
