Shader "Hidden/VelocityCalc"
{
    Properties
    {
        _HeightMap ("HeightMap", 2D) = "white" {}
        _NormalMap ("NormalMap", 2D) = "bump" {}
        _RimHeight ("RimHeight", 2D) = "white" {}
        _Gravity ("Gravity (x,y,z)", Vector) = (0,0,0,0)
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

            float4 _Gravity;    // x,y used
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
                // assume height is stored in .r and in reasonable 0..1 range; clamp to avoid extremes
                float h = tex2D(_HeightMap, uv).r;
                return saturate(h); // clamp to [0,1]
            }

            // central difference gradient in texture space (units = height per texel)
            inline float2 SampleHeightGradient(float2 uv)
            {
                float2 ts = _TexelSize.xy;
                // offsets in uv
                float2 offX = float2(ts.x, 0);
                float2 offY = float2(0, ts.y);

                float hL = tex2D(_HeightMap, uv - offX).r;
                float hR = tex2D(_HeightMap, uv + offX).r;
                float hD = tex2D(_HeightMap, uv - offY).r;
                float hU = tex2D(_HeightMap, uv + offY).r;

                // central diff
                float hx = (hR - hL) / (2.0 * ts.x + 1e-6);
                float hy = (hU - hD) / (2.0 * ts.y + 1e-6);
                return float2(hx, hy);
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // read height and rim
                float h = SampleHeight(uv);
                float rimH = tex2D(_RimHeight, uv).r;

                // if rim map is present and rimH <= 0 treat as outside (no flow)
                if (rimH <= 0.0)
                {
                    // Output zero velocity outside pan area
                    return float4(0.0, 0.0, 0.0, 1.0);
                }

                // clamp height by rim height if rimH > 0
                h = min(h, rimH);

                // normal (stored in 0..1, convert to -1..1 and normalize)
                float4 n4 = tex2D(_NormalMap, uv);
                float3 N = normalize(n4.xyz * 2.0 - 1.0);

                // gradient
                float2 grad = SampleHeightGradient(uv);

                // 既に G = float3(_Gravity.x, _Gravity.y, _Gravity.z) を取っている想定
float3 G = float3(_Gravity.x, _Gravity.y, _Gravity.z);

// project gravity onto tangent plane
float3 g_tangent3 = G - N * dot(G, N);

// MAPPING: テクスチャの V が pan-local Z を表す場合は x,z を使う
float2 g_tangent = float2(g_tangent3.x, g_tangent3.z);


                // base velocity estimate: slope-driven + gravity-driven
                // negative gradient -> flow toward lower height
                float2 v = -_kSlope * grad + _kGravity * g_tangent * sqrt(max(h, 1e-6));

                // damping to reduce high-frequency oscillations
                v *= _Damping;

                // clamp to max velocity
                float len = length(v);
                if (len > _maxVel && len > 1e-9)
                {
                    v = v * (_maxVel / len);
                }

                // pack into RG (BA unused, A=1 for convenience)
                return float4(v.x, v.y, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
