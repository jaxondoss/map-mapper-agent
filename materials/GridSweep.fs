/*{
    "DESCRIPTION": "A glowing grid that swings in on a hinge and lands flat on the surface, like a calibration grid snapping onto a wall. Drive 'progress' 0 to 1 to play the swing.",
    "CREDIT": "madmapper-agent",
    "ISFVSN": "2",
    "CATEGORIES": ["Generator", "Projection Mapping"],
    "INPUTS": [
        { "NAME": "progress", "LABEL": "Progress", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "hinge", "LABEL": "Hinge", "TYPE": "long", "DEFAULT": 0, "VALUES": [0, 1, 2, 3], "LABELS": ["Left", "Right", "Top", "Bottom"] },
        { "NAME": "cells", "LABEL": "Cells (tall)", "TYPE": "float", "DEFAULT": 8.0, "MIN": 2.0, "MAX": 40.0 },
        { "NAME": "lineWidth", "LABEL": "Line Width", "TYPE": "float", "DEFAULT": 0.006, "MIN": 0.001, "MAX": 0.03 },
        { "NAME": "glow", "LABEL": "Glow", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 3.0 },
        { "NAME": "color", "LABEL": "Color", "TYPE": "color", "DEFAULT": [0.45, 0.85, 1.0, 1.0] },
        { "NAME": "intensity", "LABEL": "Intensity", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 2.0 },
        { "NAME": "loop", "LABEL": "Loop (ignore progress)", "TYPE": "bool", "DEFAULT": false }
    ]
}*/

// Renders a plane hinged on one edge of the surface. At progress 0 it is
// swung ~80 degrees away from the viewer (into the wall); at 1 it lies flat and fills the
// surface exactly, so the grid lines up with the mapped wall.

#define FOCAL 1.6

// Swing angle for a progress value: a smooth swing that slightly overshoots
// flat (toward the viewer) and settles, like a panel snapping onto the wall.
float swingAngle(float p) {
    p = clamp(p, 0.0, 1.0);
    float ease = p * p * (3.0 - 2.0 * p);
    float settle = sin(3.14159 * smoothstep(0.72, 1.0, p));
    return 1.4 * pow(1.0 - ease, 1.3) - 0.09 * settle;
}

float hash(float n) { return fract(sin(n) * 43758.5453); }

void main() {
    vec2 res = RENDERSIZE;
    vec2 uv = (gl_FragCoord.xy - 0.5 * res) / res.y;
    vec2 half_ = vec2(0.5 * res.x / res.y, 0.5);

    // Rotate the problem so the hinge is always on the left.
    float h = float(hinge);
    if (h > 0.5 && h < 1.5) uv.x = -uv.x;
    if (h > 1.5) { uv = vec2(uv.y, uv.x); half_ = half_.yx; }
    if (h > 2.5) uv.x = -uv.x;

    float p = loop ? clamp(fract(TIME / 6.0) * 1.6, 0.0, 1.0) : progress;
    float angle = swingAngle(p);

    // Hinge line at x = -half_.x on the z = 0 plane; the plane swings in from behind it.
    vec3 ro = vec3(0.0, 0.0, FOCAL);
    vec3 rd = vec3(uv, -FOCAL);
    vec3 H = vec3(-half_.x, 0.0, 0.0);
    vec3 u = vec3(cos(angle), 0.0, -sin(angle));
    vec3 n = vec3(sin(angle), 0.0, cos(angle));

    vec3 col = vec3(0.0);
    float denom = dot(rd, n);
    if (abs(denom) > 1e-4) {
        float t = dot(H - ro, n) / denom;
        if (t > 0.0) {
            vec3 P = ro + rd * t;
            float s = dot(P - H, u);          // distance from the hinge along the plane
            vec2 q = vec2(s, P.y + half_.y);  // plane coords, 0..2*half_
            if (q.x >= 0.0 && q.x <= 2.0 * half_.x && q.y >= 0.0 && q.y <= 1.0) {
                float cell = 1.0 / cells;
                vec2 g = abs(fract(q / cell + 0.5) - 0.5) * cell;
                // border lines so the grid's edges always line up with the surface edges
                vec2 e = min(q, vec2(2.0 * half_.x, 1.0) - q);
                float dist = min(min(g.x, g.y), min(e.x, e.y));
                float px = t / (FOCAL * res.y);   // world units per pixel at this depth
                float w = max(lineWidth, px);
                float line = 1.0 - smoothstep(w * 0.5, w * 0.5 + px * 1.5, dist);
                float halo = exp(-dist / (w * 3.0)) * 0.35 * glow;
                // fade in as it starts swinging; brief flash as it lands
                float appear = smoothstep(0.0, 0.15, p);
                float land = exp(-pow((p - 0.85) * 9.0, 2.0)) * 0.6;
                float flicker = 0.92 + 0.08 * hash(floor(TIME * 24.0));
                col = color.rgb * (line + halo) * (appear + land) * flicker;
            }
        }
    }

    gl_FragColor = vec4(col * intensity, 1.0);
}
