Shader "Hidden/VelocityCalc"
{
    Properties
    {
        // 入力テクスチャ群
        _HeightMap ("HeightMap (R channel = height)", 2D) = "white" {}    // 油の厚みをRチャンネルに格納している想定
        _NormalMap ("NormalMap (RGB)", 2D) = "bump" {}                    // HeightMap から生成した法線マップ（0..1表現）
        _RimHeight ("RimHeight (mask/height)", 2D) = "white" {}           // フライパン内領域のマスクまたは縁の高さ（0で外側）
        
        // C# 側から渡すパラメータ
        // 注意: このシェーダでは _Gravity.x = panLocal.x, _Gravity.y = panLocal.z を期待する（C# 側の割当と厳密に一致させること）
        _Gravity ("Gravity (x = pan.x, y = pan.z)", Vector) = (0,0,0,0)
        
        // Texel 単位（1/width, 1/height） — 勾配の計算で使用
        _TexelSize ("TexelSize (1/width,1/height)", Vector) = (0.01,0.01,0,0)

        // チューニング用パラメータ
        _kSlope ("Slope Gain (勾配に対する感度)", Float) = 0.6
        _kGravity ("Gravity Gain (重力寄与の強さ)", Float) = 1.0
        _maxVel ("Max Velocity (速度上限)", Float) = 0.6
        _Damping ("Damping (数値減衰 0..1)", Float) = 0.95
    }

    SubShader
    {
        // フルスクリーン Blit 用。深度やカリングは不要なので無効化。
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

            // --- テクスチャ／パラメータ宣言 ---
            sampler2D _HeightMap;    // R = height
            sampler2D _NormalMap;    // RGB = normal (0..1)
            sampler2D _RimHeight;    // R = rim mask or rim height (0 = outside)

            // _Gravity は C# が (pan.x, pan.z) を x,y に入れて渡す想定
            float4 _Gravity;
            float4 _TexelSize;  // x = 1/width, y = 1/height

            float _kSlope;
            float _kGravity;
            float _maxVel;
            float _Damping;

            // --- 入力頂点構造体 / 出力構造体 ---
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

            // 頂点シェーダ — フルスクリーン四角形をそのまま透過
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex); // 標準的なクリップ変換
                o.uv = v.uv;                             // UV をフラグメントに転送
                return o;
            }

            // --------------------
            // ヘルパー関数：Heightの安全サンプル
            // - Height が極端な値を持つと勾配や sqrt(h) で数値破綻が起きることがあるため saturate で 0..1 に収める
            // --------------------
            inline float SampleHeight(float2 uv)
            {
                float h = tex2D(_HeightMap, uv).r;
                return saturate(h); // 0..1 にクランプ（安全策）
            }

            // --------------------
            // ヘルパー関数：中央差分による勾配計算（texture-space）
            // - 戻り値は (dh/du, dh/dv)（UV 空間当たりの高さ差）
            // - _TexelSize によって適切なスケール（1/width,1/height）を渡す必要あり
            // - ex, ey はゼロ割避けのため小さな下限を設ける
            // --------------------
            inline float2 SampleHeightGradient(float2 uv)
            {
                float2 ts = _TexelSize.xy;
                // テクセルサイズが 0 にならないようにフォールバック
                float ex = max(ts.x, 1e-6);
                float ey = max(ts.y, 1e-6);

                // 中央差分で周囲4点をサンプリング
                float hL = tex2D(_HeightMap, uv - float2(ex, 0)).r;
                float hR = tex2D(_HeightMap, uv + float2(ex, 0)).r;
                float hD = tex2D(_HeightMap, uv - float2(0, ey)).r;
                float hU = tex2D(_HeightMap, uv + float2(0, ey)).r;

                // 中央差分の公式
                float hx = (hR - hL) / (2.0 * ex);
                float hy = (hU - hD) / (2.0 * ey);
                return float2(hx, hy);
            }

            // --------------------
            // フラグメントシェーダ（ここが本体）
            // - 各ピクセル（UV）での速度ベクトル (vx, vy) を計算して RG に格納する
            // - 流速は「勾配項（低い方へ流す）」と「表面に沿った重力寄与」の和で見積もる
            // --------------------
            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // --------------------
                // rim（縁）判定
                // - RimHeight テクスチャの R チャンネルが 0 以下なら「パン外」とみなして速度 0 を返す
                // - 運用上、デバッグ時は rim をすべて 1 にしてマスクしないと良い（内部確認用）
                // --------------------
                float rimH = tex2D(_RimHeight, uv).r;
                if (rimH <= 0.0) {
                    // 外側は速度無し（黒）
                    return float4(0.0, 0.0, 0.0, 1.0);
                }

                // --------------------
                // 高さ h をサンプルして rim の高さで clamp
                // - h が非常に小さいと sqrt(h) により重力寄与がほぼ消える点に注意（必要なら最低 h_min を導入）
                // --------------------
                float h = SampleHeight(uv);
                h = min(h, rimH);

                // --------------------
                // 法線読み取り・正規化
                // - NormalMap は通常 0..1 に格納されるので *2-1 して -1..1 に戻す
                // - 必ず normalize してから使う（正規化忘れは投影で誤差を生む）
                // - 小さな数値での不安定を避けるため tiny を足しておく（normalize の安定化）
                // --------------------
                float4 n4 = tex2D(_NormalMap, uv);
                // n4.xyz in 0..1 space -> convert to -1..1
                float3 N = normalize(n4.xyz * 2.0 - 1.0 + 1e-6);

                // --------------------
                // 高さの勾配（uv空間）を取得
                // - grad は (dh/du, dh/dv) で、負号をつければ「低い方へ流れる」方向になる
                // --------------------
                float2 grad = SampleHeightGradient(uv);

                // --------------------
                // 重力ベクトルの再構築（重要）
                // - C# 側の契約: _Gravity.x = panLocal.x, _Gravity.y = panLocal.z
                // - ここでは tex U に対応させるため 3D ベクトル (gx, 0, gz) を作る
                //   （Y方向がテクスチャ平面の法線方向として扱われるため中間の要素を 0 に）
                // - ここが C# の詰め方と食い違うと「上下傾きが反映されない」等の問題が起きる
                // --------------------
                float3 G = float3(_Gravity.x, 0.0, _Gravity.y);

                // --------------------
                // 重力を表面の接線（tangent）成分に射影する
                // - g_tangent3 = G - N * dot(G, N)
                //   つまり G のうち法線方向成分を取り除き、面に沿う成分のみを残す
                // - 返すのはテクスチャ座標に対応する2成分 (x,z)
                // --------------------
                float3 g_tangent3 = G - N * dot(G, N);
                float2 g_tangent = float2(g_tangent3.x, g_tangent3.z);

                // --------------------
                // 速度の見積もり
                // - 勾配項: -_kSlope * grad    -> 低い方向へ移動させる
                // - 重力項: _kGravity * g_tangent * sqrt(h)
                //   sqrt(h) を掛けることで薄い液体では重力寄与が弱く、厚みが増すと流れやすくする
                //   （物理厳密性は妥協した経験則的なモデル）
                // - 数値安定化: sqrt の引数には小さな eps を入れる
                // --------------------
                float2 v = -_kSlope * grad + _kGravity * g_tangent * sqrt(max(h, 1e-6));

                // --------------------
                // ダンピングとクランプ（過大な値や高周波を抑える）
                // - v *= _Damping: 各フレームで少し減衰させることで振動を抑制
                // - 上限 _maxVel により極端な値を切る（安全策）
                // --------------------
                v *= _Damping;
                float len = length(v);
                if (len > _maxVel && len > 1e-9)
                    v = v * (_maxVel / len);

                // --------------------
                // 出力：R= vx, G= vy, B unused, A=1
                // - C# 側はこの RT を ReadPixels/GetPixels 等で読み、粒子の移動に使用する
                // --------------------
                return float4(v.x, v.y, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
