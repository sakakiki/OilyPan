Shader "Hidden/VelocityCalc_Visualizer"
{
    Properties
    {
        _HeightMap ("HeightMap", 2D) = "white" {}
        _NormalMap ("NormalMap", 2D) = "bump" {}
        _RimHeight ("RimHeight", 2D) = "white" {}
        _Gravity ("Gravity (x,y)", Vector) = (0,0,0,0)
        _TexelSize ("TexelSize", Vector) = (0.01,0.01,0,0)
        _kSlope ("Slope Gain", Float) = 0.6
        _kGravity ("Gravity Gain", Float) = 1.0
        _maxVel ("Max Velocity", Float) = 0.6
        _Damping ("Damping (0..1)", Float) = 0.95

        _Mode ("Mode (0=vel,1=G,2=g_tangent,3=grad,4=normal,5=|v|)", Float) = 0
        _Scale ("Scale", Float) = 1.0
    }
    SubShader {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass {
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
            float4 _Gravity;
            float4 _TexelSize;
            float _kSlope;
            float _kGravity;
            float _maxVel;
            float _Damping;
            float _Mode;
            float _Scale;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float2 uv : TEXCOORD0; float4 pos : SV_POSITION; };

            v2f vert(appdata v){ v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }

            inline float SampleH(float2 uv){ return saturate(tex2D(_HeightMap, uv).r); }
            inline float2 SampleGrad(float2 uv){
                float2 ts = _TexelSize.xy; float ex = max(ts.x,1e-6); float ey = max(ts.y,1e-6);
                float hL = tex2D(_HeightMap, uv - float2(ex,0)).r; float hR = tex2D(_HeightMap, uv + float2(ex,0)).r;
                float hD = tex2D(_HeightMap, uv - float2(0,ey)).r; float hU = tex2D(_HeightMap, uv + float2(0,ey)).r;
                return float2((hR-hL)/(2.0*ex), (hU-hD)/(2.0*ey));
            }

            float4 frag(v2f i):SV_Target {
                float2 uv = i.uv;
                float rimH_raw = tex2D(_RimHeight, uv).r;
                float rimH = (rimH_raw > 0.0001) ? rimH_raw : 1.0;
                float h = min(SampleH(uv), rimH);

                float4 n4 = tex2D(_NormalMap, uv);
                float3 N_raw = n4.xyz * 2.0 - 1.0;
                float3 N = (length(N_raw) > 1e-4) ? normalize(N_raw) : float3(0,1,0);

                float2 grad = SampleGrad(uv);
                float3 G = float3(_Gravity.x, 0.0, _Gravity.y);
                float3 g_tangent3 = G - N * dot(G, N);
                float2 g_tangent = float2(g_tangent3.x, g_tangent3.z);
                float2 v = -_kSlope * grad + _kGravity * g_tangent * sqrt(max(h,1e-6));
                v *= _Damping;
                float vlen = length(v);
                if(vlen > _maxVel && vlen > 1e-9) v *= (_maxVel / vlen);

                // visualization modes
                if (_Mode < 0.5) {
                    // mode 0: show velocity (as color) — map signed to 0..1 with center 0.5
                    float2 vis = v * _Scale;
                    return float4(vis.x*0.5+0.5, vis.y*0.5+0.5, 0, 1);
                } else if (_Mode < 1.5) {
                    float2 vis = float2(_Gravity.x, _Gravity.y) * _Scale;
                    return float4(vis.x*0.5+0.5, vis.y*0.5+0.5, 0, 1);
                } else if (_Mode < 2.5) {
                    float2 vis = g_tangent * _Scale;
                    return float4(vis.x*0.5+0.5, vis.y*0.5+0.5, 0, 1);
                } else if (_Mode < 3.5) {
                    float2 vis = grad * _Scale;
                    return float4(vis.x*0.5+0.5, vis.y*0.5+0.5, 0, 1);
                } else if (_Mode < 4.5) {
                    float3 nc = N * 0.5 + 0.5;
                    return float4(nc, 1);
                } else {
                    float m = vlen * _Scale;
                    return float4(m,m,m,1);
                }
            }
            ENDCG
        }
    }
}
