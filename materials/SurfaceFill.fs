/*{
    "DESCRIPTION": "Fills a surface with light, wiping in from one side. Put it on an object such as a lampshade to 'switch it on' with projected light. Drive 'progress' 0 to 1 to play the wipe.",
    "CREDIT": "madmapper-agent",
    "ISFVSN": "2",
    "CATEGORIES": ["Generator", "Projection Mapping"],
    "INPUTS": [
        { "NAME": "progress", "LABEL": "Progress", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "direction", "LABEL": "Direction", "TYPE": "long", "DEFAULT": 0, "VALUES": [0, 1, 2, 3, 4], "LABELS": ["Left to right", "Right to left", "Bottom to top", "Top to bottom", "Center out"] },
        { "NAME": "softness", "LABEL": "Edge Softness", "TYPE": "float", "DEFAULT": 0.15, "MIN": 0.0, "MAX": 0.5 },
        { "NAME": "color", "LABEL": "Color", "TYPE": "color", "DEFAULT": [1.0, 0.96, 0.88, 1.0] },
        { "NAME": "intensity", "LABEL": "Intensity", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 2.0 },
        { "NAME": "edgeGlow", "LABEL": "Leading Edge Glow", "TYPE": "float", "DEFAULT": 0.6, "MIN": 0.0, "MAX": 2.0 },
        { "NAME": "edgeColor", "LABEL": "Edge Color", "TYPE": "color", "DEFAULT": [0.45, 0.85, 1.0, 1.0] },
        { "NAME": "loop", "LABEL": "Loop (ignore progress)", "TYPE": "bool", "DEFAULT": false }
    ]
}*/

void main() {
    vec2 uv = isf_FragNormCoord;
    float d = float(direction);
    float x = uv.x;                                   // 0 where the wipe starts, 1 where it ends
    if (d > 0.5 && d < 1.5) x = 1.0 - uv.x;
    if (d > 1.5 && d < 2.5) x = uv.y;
    if (d > 2.5 && d < 3.5) x = 1.0 - uv.y;
    if (d > 3.5) x = length((uv - 0.5) * vec2(RENDERSIZE.x / RENDERSIZE.y, 1.0)) * 1.4;

    float p = loop ? clamp(fract(TIME / 4.0) * 1.5, 0.0, 1.0) : progress;
    float front = p * (1.0 + softness);               // overshoot so 1.0 is fully lit
    float lit = 1.0 - smoothstep(front - softness, front, x);
    float edge = exp(-pow((x - front + softness * 0.5) / max(softness * 0.5 + 0.02, 0.02), 2.0)) * step(p, 0.999);

    vec3 col = color.rgb * lit + edgeColor.rgb * edge * edgeGlow;
    gl_FragColor = vec4(col * intensity, 1.0);
}
