# Offline platform icon assets

These PNGs are bundled local resources for the WPF client. No network request is made at runtime.

## Format and rendering

- Every file is a 128x128 RGBA PNG with a transparent canvas.
- The source mark is fitted into a 104x104 box and centered, so all platforms have the same optical inset.
- Simple Icons sources use the fixed `16.29.0` package and upstream commit `e3d830c3b553bb657df7389b673d1d78abf5159b`.
- Simple Icons source SVGs are 24x24 viewBox marks. PNG rasterization used a transparent background and the listed display color.
- TikTok and X use a white display color for contrast on EasyGet's dark surface; their Simple Icons metadata color is black. The mark geometry is unchanged.

## Sources and integrity

| File | Platform | Display color | Source asset (fixed URL) | Brand/source guidance | Source SHA-256 | PNG SHA-256 |
| --- | --- | --- | --- | --- | --- | --- |
| `youtube.png` | YouTube | `#FF0000` | [Simple Icons SVG](https://raw.githubusercontent.com/simple-icons/simple-icons/e3d830c3b553bb657df7389b673d1d78abf5159b/icons/youtube.svg) | [YouTube brand resources](https://www.youtube.com/howyoutubeworks/resources/brand-resources/#logos-icons-and-colors) | `5038808acbbc4e6edda16cbeb1cc6dec80e4e4ee4e227e039c41229fa222aa8c` | `e0f625f91cd770674b3856dc2b1307f3026f268c011881932ddd31a57115c916` |
| `bilibili.png` | Bilibili | `#00A1D6` | [Simple Icons SVG](https://raw.githubusercontent.com/simple-icons/simple-icons/e3d830c3b553bb657df7389b673d1d78abf5159b/icons/bilibili.svg) | [Bilibili](https://www.bilibili.com) | `0b31005ccf765a9d37be2c42f1d36e44572ccadb088250626d9905ae5d4f3d21` | `88dd2a924b592e1e8f409c08c8384ca47be3782b61adeb56e8bf5d640291484f` |
| `douyin.png` | Douyin | official multicolor mark | [Douyin official PWA icon](https://lf3-static.bytednsdoc.com/obj/eden-cn/666eh7nuhfvhpebd/douyin_web/douyin_web/pwa_v3/512_512.png) | Official Douyin CDN asset; no separate open license was supplied | `430af333c6b42edabb229902e107ff8281c7c078662b7e507d30955d2d070128` | `b846ee55b16feadbc5de2e63bac40eb334a06c7ab399c6b51ac2423388177121` |
| `tiktok.png` | TikTok | `#FFFFFF` (source metadata `#000000`) | [Simple Icons SVG](https://raw.githubusercontent.com/simple-icons/simple-icons/e3d830c3b553bb657df7389b673d1d78abf5159b/icons/tiktok.svg) | [TikTok](https://tiktok.com) | `6f54ac8d325faacea8935bdc44cbed60206a6b408641799e5fea1cba7c1a0af7` | `17cc84ba891576c9680dcf4f4f92390d24002ae2429f6f61e1cfa9b7f4eae42c` |
| `x-twitter.png` | X / Twitter | `#FFFFFF` (source metadata `#000000`) | [Simple Icons SVG](https://raw.githubusercontent.com/simple-icons/simple-icons/e3d830c3b553bb657df7389b673d1d78abf5159b/icons/x.svg) | [X brand toolkit](https://about.x.com/en/who-we-are/brand-toolkit) | `693e68863eceb8dc9f72e2acd386ab9c20a10858dab2c076212f7084cb7a32fe` | `89a2a28b2f3dac1f1cd7f36ce9f7062a12e847704ebeeba63a6da8e44f3d4652` |
| `instagram.png` | Instagram | `#FF0069` | [Simple Icons SVG](https://raw.githubusercontent.com/simple-icons/simple-icons/e3d830c3b553bb657df7389b673d1d78abf5159b/icons/instagram.svg) | [Instagram brand resources](https://about.meta.com/brand/resources/instagram) | `f53af2d1fc5292ba1433b5c1faf50005ce6a997fa302d1816989929f379a59dc` | `e7103573f37160a7fb8f9ed4b3f0bb83b333286ff35dddf147524230b812bd26` |
| `facebook.png` | Facebook | `#0866FF` | [Simple Icons SVG](https://raw.githubusercontent.com/simple-icons/simple-icons/e3d830c3b553bb657df7389b673d1d78abf5159b/icons/facebook.svg) | [Facebook brand resources](https://about.meta.com/brand/resources/facebook/logo) | `b06d18d844ed621b89faffb1a33440cc0ec4f1ffea9f36191f50db19a47c59a6` | `a6966bffeccd3d4aa2df961580767f06f48fd4c26e0c22cd6b387aadcec22079` |
| `kuaishou.png` | Kuaishou | `#FF4906` | [Simple Icons SVG](https://raw.githubusercontent.com/simple-icons/simple-icons/e3d830c3b553bb657df7389b673d1d78abf5159b/icons/kuaishou.svg) | [Kuaishou material library](https://www.kuaishou.com/official/material-lib) | `6fd3afc958574cf6aafafa5a4085d7903dec61d20e0ee737f632b0c815da764e` | `e709b9741cc5c9091072f20871e006f7c09ecb1edc3704a2f096dcb1a3a86f24` |
| `xiaohongshu.png` | Xiaohongshu | `#FF2442` | [Simple Icons SVG](https://raw.githubusercontent.com/simple-icons/simple-icons/e3d830c3b553bb657df7389b673d1d78abf5159b/icons/xiaohongshu.svg) | [Xiaohongshu](https://pro.xiaohongshu.com) | `847cacc807f5c236031a7477381daa67fd829d89f3f80c16a547ff2efe050794` | `197ddfa2154a7d4e98df10bb62068dcbf7b4a595c435e78d4af9cf2948cb3e37` |
| `weibo.png` | Weibo | `#E6162D` | [Simple Icons SVG](https://raw.githubusercontent.com/simple-icons/simple-icons/e3d830c3b553bb657df7389b673d1d78abf5159b/icons/sinaweibo.svg) | [Sina Weibo reference](https://en.wikipedia.org/wiki/Sina_Weibo) | `44facf47c6bfa06c312c28acda6aa2ed404ef7f68c2897c8f33ad51b6ba94059` | `00abe21cdc5118ede59e494cd9963600c84bd76151b562a5cd4e380bdee9b318` |
| `twitch.png` | Twitch | `#9146FF` | [Simple Icons SVG](https://raw.githubusercontent.com/simple-icons/simple-icons/e3d830c3b553bb657df7389b673d1d78abf5159b/icons/twitch.svg) | [Twitch brand](https://brand.twitch.tv) | `dbe27bc02a01b8d89b6259cacef328c9fa70bb13c29f38c36eb1421863f6f0b6` | `ca839dfce4711a097b007045521e68ca748242097159a36392cf4961088ae61f` |

## License and trademark notice

The Simple Icons source artwork is distributed under [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) (see the [upstream license](https://raw.githubusercontent.com/simple-icons/simple-icons/e3d830c3b553bb657df7389b673d1d78abf5159b/LICENSE.md)). Brand names, logos, and trade dress remain the property of their respective owners and may be subject to trademark rules or separate brand guidelines. The Douyin PWA artwork is an official website-delivered asset and is included only for nominative platform identification; no additional copyright license is implied. EasyGet does not claim affiliation with any listed platform.
