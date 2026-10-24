/*{
    "DESCRIPTION": "Hangs a real picture frame from a glowing projected chain. Two chains drop from the top edge to a clasp on the frame; the wall around it is softly lit and a swaying shadow sells the illusion that the frame hangs and swings. Line 'frameCenter' and 'frameSize' up with the real frame, then drive 'drop' 0 to 1 to lower the chain.",
    "CREDIT": "madmapper-agent",
    "ISFVSN": "2",
    "CATEGORIES": ["Generator", "Projection Mapping"],
    "INPUTS": [
        { "NAME": "drop", "LABEL": "Drop", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "frameCenter", "LABEL": "Frame Center", "TYPE": "point2D", "DEFAULT": [0.5, 0.4], "MIN": [0.0, 0.0], "MAX": [1.0, 1.0] },
        { "NAME": "frameSize", "LABEL": "Frame Size (x height)", "TYPE": "point2D", "DEFAULT": [0.3, 0.38], "MIN": [0.02, 0.02], "MAX": [2.0, 1.0] },
        { "NAME": "spread", "LABEL": "Chain Spread", "TYPE": "float", "DEFAULT": 0.7, "MIN": 0.0, "MAX": 2.0 },
        { "NAME": "linkSize", "LABEL": "Link Size", "TYPE": "float", "DEFAULT": 0.045, "MIN": 0.015, "MAX": 0.12 },
        { "NAME": "sway", "LABEL": "Sway", "TYPE": "float", "DEFAULT": 0.5, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "swingSpeed", "LABEL": "Swing Speed", "TYPE": "float", "DEFAULT": 1.2, "MIN": 0.0, "MAX": 4.0 },
        { "NAME": "color", "LABEL": "Chain Color", "TYPE": "color", "DEFAULT": [0.3, 0.75, 1.0, 1.0] },
        { "NAME": "glow", "LABEL": "Glow", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 3.0 },
        { "NAME": "wallLight", "LABEL": "Wall Light", "TYPE": "float", "DEFAULT": 0.35, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "wallColor", "LABEL": "Wall Light Color", "TYPE": "color", "DEFAULT": [1.0, 0.95, 0.85, 1.0] },
        { "NAME": "shadow", "LABEL": "Shadow", "TYPE": "float", "DEFAULT": 0.85, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "shadowOffset", "LABEL": "Shadow Offset", "TYPE": "point2D", "DEFAULT": [0.05, -0.07], "MIN": [-0.3, -0.3], "MAX": [0.3, 0.3] },
        { "NAME": "maskFrame", "LABEL": "Don't Light The Frame", "TYPE": "bool", "DEFAULT": true },
        { "NAME": "intensity", "LABEL": "Intensity", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 2.0 },
        { "NAME": "loop", "LABEL": "Loop (ignore drop)", "TYPE": "bool", "DEFAULT": false }
    ]
}*/

// Coordinates are in surface-height units: x in [0, aspect], y in [0, 1], y up.

float sdStadium(vec2 p, vec2 h) {
    // capsule-ended rectangle: half length h.x, half thickness h.y
    p = abs(p);
    p.x = max(p.x - (h.x - h.y), 0.0);
    return length(p) - h.y;
}

float sdBox(vec2 p, vec2 b) {
    vec2 d = abs(p) - b;
    return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0);
}

vec2 rot(vec2 p, float a) {
    return vec2(cos(a) * p.x - sin(a) * p.y, sin(a) * p.x + cos(a) * p.y);
}

// One strand of chain from the clasp m up to the top point a.
// Returns (distance, stroke-relative depth for shading).
vec2 strand(vec2 p, vec2 m, vec2 a, float dropAmt, float phase) {
    vec2 axis = a - m;
    float len = length(axis);
    vec2 dir = axis / len;
    vec2 nrm = vec2(-dir.y, dir.x);
    float along = dot(p - m, dir);
    float across = dot(p - m, nrm);

    // gentle sway, zero at both ends
    float s = clamp(along / len, 0.0, 1.0);
    across -= sway * 0.012 * sin(3.14159 * s) * sin(TIME * swingSpeed * 1.7 + phase);

    // the chain's lower end slides down from above as it drops
    float hidden = (1.0 - dropAmt) * (len + 0.2);
    float la = along - hidden;
    if (la < -linkSize || along > len + linkSize) return vec2(1e3, 0.0);

    float spacing = linkSize;
    float th = spacing * 0.13;
    float k0 = floor(la / spacing);
    float best = 1e3;
    for (int j = -1; j <= 1; j++) {
        float k = k0 + float(j);
        if (k < 0.0) continue;
        vec2 q = vec2(la - (k + 0.5) * spacing, across);
        float d;
        if (mod(k, 2.0) < 0.5) {
            d = abs(sdStadium(q, vec2(spacing * 0.68, spacing * 0.3))) - th;   // face-on ring
        } else {
            d = sdStadium(q, vec2(spacing * 0.68, th * 1.15));                  // edge-on link
        }
        best = min(best, d);
    }
    return vec2(best, clamp(-best / th, 0.0, 1.0));
}

vec2 chain(vec2 p, vec2 m, float dropAmt, float halfSpread) {
    vec2 a1 = vec2(m.x - halfSpread, 1.05);
    vec2 a2 = vec2(m.x + halfSpread, 1.05);
    vec2 c1 = strand(p, m, a1, dropAmt, 0.0);
    vec2 c2 = strand(p, m, a2, dropAmt, 1.7);
    return c1.x < c2.x ? c1 : c2;
}

// The clasp: a small ring where the strands meet and a bail widening down to the frame.
float clasp(vec2 p, vec2 m, vec2 frameTop) {
    float ring = abs(length(p - m) - linkSize * 0.22) - linkSize * 0.09;
    vec2 q = p - vec2(frameTop.x, (m.y + frameTop.y) * 0.5);
    float hgt = (m.y - frameTop.y) * 0.5;
    float w = mix(linkSize * 0.55, linkSize * 0.18, clamp((q.y + hgt) / (2.0 * hgt), 0.0, 1.0));
    float bail = max(abs(q.x) - w, abs(q.y) - hgt);
    return min(ring, bail);
}

void main() {
    float aspect = RENDERSIZE.x / RENDERSIZE.y;
    vec2 p = vec2(isf_FragNormCoord.x * aspect, isf_FragNormCoord.y);
    float px = 1.0 / RENDERSIZE.y;

    float dropAmt = loop ? smoothstep(0.0, 2.5, mod(TIME, 10.0)) : drop;

    vec2 fc = vec2(frameCenter.x * aspect, frameCenter.y);
    vec2 fh = frameSize * 0.5;
    vec2 frameTop = vec2(fc.x, fc.y + fh.y);
    vec2 m = frameTop + vec2(0.0, linkSize * 1.3);
    float halfSpread = spread * fh.x;

    // ---- wall light with a swaying shadow of the frame (and chain)
    float swing = sway * 0.06 * sin(TIME * swingSpeed) * smoothstep(0.6, 1.0, dropAmt);
    vec2 sp = rot(p - m - shadowOffset, -swing) + m;      // swing the shadow about the clasp
    float sFrame = sdBox(sp - fc, fh);
    float sChain = chain(sp, m, dropAmt, halfSpread).x;
    float soft = 0.012;
    float shade = max(1.0 - smoothstep(-soft, soft, sFrame), (1.0 - smoothstep(-soft, soft, sChain)) * 0.7);
    vec3 col = wallColor.rgb * wallLight * (1.0 - shadow * shade * smoothstep(0.5, 1.0, dropAmt));

    if (maskFrame && sdBox(p - fc, fh + 0.004) < 0.0) col = vec3(0.0);

    // ---- chain
    vec2 c = chain(p, m, dropAmt, halfSpread);
    float claspD = clasp(p, m, frameTop);
    float claspOn = smoothstep(0.85, 1.0, dropAmt);
    float d = min(c.x, mix(1e3, claspD, claspOn));
    float depth = c.x < claspD ? c.y : clamp(-claspD / (linkSize * 0.1), 0.0, 1.0);

    float fill = 1.0 - smoothstep(-px, px, d);
    vec3 chainCol = color.rgb * (0.65 + 0.35 * depth) + vec3(0.35) * pow(depth, 3.0);
    col = mix(col, chainCol, fill);
    col += color.rgb * exp(-max(d, 0.0) / (linkSize * 0.25)) * 0.35 * glow * (1.0 - fill);

    gl_FragColor = vec4(col * intensity, 1.0);
}
