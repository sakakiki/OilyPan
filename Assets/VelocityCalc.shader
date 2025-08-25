Shader "Hidden/VelocityCalc"
{
    Properties
    {
        _HeightMap ("HeightMap", 2D) = "white" {}
        _NormalMap ("NormalMap", 2D) = "bump" {}
        _RimHeight ("RimHeight", 2D) = "white" {}
        _Gravity ("Gravity (x,y)", Vector) = (0,0,0,0) // x = pan.x, y = pan.z (mapped below)
        _TexelSize ("TexelSize (1/width,1/height)", Vector) = (0.01,0.01,0,0)
        _kSlope ("Slope Gain", Float) = 0.6
        _kGravity ("Gravity Gain", Float) = 1.0
        _maxVel ("Max Velocity", Float) = 0.6
        _Damping ("Damping (0..1)", Float) = 0.95
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            ZWrite Off
            Cull Off
            ZTest Always

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            sampler2D _HeightMap;
            sampler2D _NormalMap;
            sampler2D _RimHeight;

            float4 _Gravity;    // C# passes: x = pan.x, y = pan.z
            float4 _TexelSize;  // x = 1/width, y = 1/height
            float _kSlope;
            float _kGravity;
            float _maxVel;
            float _Damping;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // sample height with clamp safety
            inline float SampleHeight(float2 uv)
            {
                float h = tex2D(_HeightMap, uv).r;
                return saturate(h); // clamp to [0,1] for safety
            }

            // central difference gradient in texture-space (units: height / uv)
            inline float2 SampleHeightGradient(float2 uv)
            {
                float2 ts = _TexelSize.xy;
                // fallback if texel size is zero (avoid div by zero)
                float ex = max(ts.x, 1e-6);
                float ey = max(ts.y, 1e-6);

                float hL = tex2D(_HeightMap, uv - float2(ex, 0)).r;
                float hR = tex2D(_HeightMap, uv + float2(ex, 0)).r;
                float hD = tex2D(_HeightMap, uv - float2(0, ey)).r;
                float hU = tex2D(_HeightMap, uv + float2(0, ey)).r;

                float hx = (hR - hL) / (2.0 * ex);
                float hy = (hU - hD) / (2.0 * ey);
                return float2(hx, hy);
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // rim height: if not provided, assume inside pan (1.0)
                float rimH = tex2D(_RimHeight, uv).r;
                if (rimH <= 0.0) {
                    // Either outside pan or rimTexture uses 0 to indicate outside.
                    // If you want no rim mask, ensure rim texture is filled with >0 values.
                    return float4(0.0, 0.0, 0.0, 1.0);
                }

                // height (clamped)
                float h = SampleHeight(uv);
                h = min(h, rimH);

                // normal: convert 0..1 -> -1..1 and normalize
                float4 n4 = tex2D(_NormalMap, uv);
                float3 N = normalize(n4.xyz * 2.0 - 1.0 + 1e-6);

                // gradient in uv space
                float2 grad = SampleHeightGradient(uv);

                // === CRUCIAL: map 2D passed gravity into 3D correctly ===
                // C# passes: _Gravity.x = panLocal.x, _Gravity.y = panLocal.z
                // Build 3D gravity vector in pan-local coords: (gx, 0, gz)
                float3 G = float3(_Gravity.x, 0.0, _Gravity.y);

                // project gravity onto tangent plane of the surface
                float3 g_tangent3 = G - N * dot(G, N);

                // mapping: texture U corresponds to pan-local X, texture V corresponds to pan-local Z
                float2 g_tangent = float2(g_tangent3.x, g_tangent3.z);

                // base velocity: flow toward lower height + gravity along surface
                float2 v = -_kSlope * grad + _kGravity * g_tangent * sqrt(max(h, 1e-6));

                // damping to reduce numerical oscillation
                v *= _Damping;

                // clamp
                float len = length(v);
                if (len > _maxVel && len > 1e-9)
                    v = v * (_maxVel / len);

                // pack into RG
                return float4(v.x, v.y, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
