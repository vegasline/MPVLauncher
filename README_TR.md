<h1 align="center">MPVLauncher</h1>

<p align="center">TarayÄ±cÄ±daki her videoyu <a href="https://mpv.io/"><b>mpv</b></a>'de tek tÄ±kla oynatÄ±n.</p>

<p align="center">
<a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-yellow.svg" alt="MIT LisansÄ±"></a>
<a href="https://github.com/vegasline/MPVLauncher/releases/latest"><img src="https://img.shields.io/badge/app-1.9.5-blue.svg" alt="Uygulama sÃ¼rÃ¼mÃ¼ 1.9.4"></a>
<a href="https://addons.mozilla.org/firefox/addon/mpv-launcher/"><img src="https://img.shields.io/badge/Firefox_Eklentisi-FF7139FF?logo=firefox-browser&logoColor=white" alt="Firefox eklentisi"></a>
</p>

<p align="center"><a href="README.md">ðŸŒ English</a> Â· TÃ¼rkÃ§e</p>

> ðŸ¤– Bu proje yapay zekÃ¢ desteÄŸiyle yazÄ±ldÄ± â€” uygulama, eklenti, dokÃ¼mantasyon ve
> testler â€” ve bir insan tarafÄ±ndan gÃ¶zden geÃ§irildi. Kodu okumaya, Ã§alÄ±ÅŸtÄ±rmaya
> ve deÄŸiÅŸtirmeye aÃ§Ä±ktÄ±r.

---

## ðŸ“¸ NasÄ±l gÃ¶rÃ¼nÃ¼yor

| ðŸ–¥ï¸ MasaÃ¼stÃ¼ uygulamasÄ± | ðŸ§© TarayÄ±cÄ± eklentisi |
| :---: | :---: |
| ![MPVLauncher ana pencere](MPVLauncher.png) | ![MPVLauncher eklenti aÃ§Ä±lÄ±r menÃ¼sÃ¼](Extension.png) |

---

## ðŸŽ¬ Ne yapar

MPVLauncher, [mpv](https://mpv.io/) iÃ§in kÃ¼Ã§Ã¼k bir baÅŸlatÄ±cÄ±dÄ±r; yanÄ±nda da web'deki
videoyu doÄŸrudan oynatÄ±cÄ±nÄ±za gÃ¶nderen bir tarayÄ±cÄ± eklentisi bulunur.

Normalde tarayÄ±cÄ±dan bir video aÃ§mak iÃ§in indirme aracÄ±, betik ya da sayfaya Ã¶zel
bir yÃ¶ntem gerekir. Bu proje bunu tek tÄ±kla hÃ¢le getirir.

- ðŸ“¥ **mpv'nin ihtiyacÄ± olanlarÄ± kurar** â€” mpv, yt-dlp ve FFmpeg'i kendi resmÃ®
  yayÄ±nlarÄ±ndan. Python yok, harici Ã§alÄ±ÅŸma zamanÄ± yok, sisteme kurulum yok.
- ðŸ”Œ **Eklentiyi kurar** â€” Chrome, Edge, Brave, Opera, Vivaldi, Yandex ve Firefox
  iÃ§in; uygulama klasÃ¶rÃ¼ taÅŸÄ±nÄ±rsa onarÄ±r.
- ðŸŽ¨ **Temalar ve gÃ¶rÃ¼nÃ¼m dÃ¼zenleyici** â€” 4 tema, Ã¶zel renkler, bulanÄ±klÄ±k, kÃ¶ÅŸe
  yuvarlaklÄ±ÄŸÄ± ve arka plan gÃ¶rseli.
- âœ¨ **Anime4K ve ModernZ** â€” anime yÃ¼kseltme ve daha gÃ¼zel ekran Ã¼stÃ¼ kontrol.
- ðŸ—‚ï¸ **Oynatma geÃ§miÅŸi** â€” son 50 kayÄ±t, eklentiyle paylaÅŸÄ±lÄ±r.
- ðŸŒ **12 dil** uygulamada, **13 dil** eklentide.

### ðŸ” Eklenti neleri bulur

- **BaÄŸÄ±msÄ±z iki algÄ±lama katmanÄ±.** Bir aÄŸ dinleyicisi sayfanÄ±n yaptÄ±ÄŸÄ± her medya
  isteÄŸini yakalar; bir iÃ§erik betiÄŸi henÃ¼z yÃ¼klenmemiÅŸ akÄ±ÅŸlarÄ± iÅŸaretleme iÃ§inde
  arar. SonuÃ§lar birleÅŸtirilir, bÃ¶ylece tembel yÃ¼klenen bir oynatÄ±cÄ±nÄ±n arkasÄ±nda
  kalan akÄ±ÅŸÄ± kaÃ§Ä±rmazsÄ±nÄ±z.
- **YaygÄ±n durumlar** â€” HLS/m3u8, DASH, doÄŸrudan dosyalar, `<video>` ve `<source>`,
  `object`/`embed` oynatÄ±cÄ±larÄ±, JSON-LD, blob ve MSE adresleri.
- **iframe'ler** â€” baÅŸka siteden gelen gÃ¶mÃ¼lÃ¼ oynatÄ±cÄ±lar listelenir; baÅŸka bir
  sayfadaki YouTube gÃ¶mmesi de Ã§alÄ±ÅŸÄ±r.
- **Filtreler** â€” akÄ±ÅŸlarÄ±, dosyalarÄ± ve iframe'leri ayrÄ± aÃ§Ä±p kapatma, en kÃ¼Ã§Ã¼k
  boyut belirleme, ilgilenmediÄŸiniz sunucularÄ± gizleme.
- **Daha anlaÅŸÄ±lÄ±r hatalar** â€” bir captcha veya Cloudflare doÄŸrulamasÄ± "boÅŸ sayfa"
  yerine olduÄŸu gibi bildirilir.

---

## ðŸš€ HÄ±zlÄ± baÅŸlangÄ±Ã§

1. [Son sÃ¼rÃ¼mden](https://github.com/vegasline/MPVLauncher/releases/latest)
   **MPVLauncher.exe** dosyasÄ±nÄ± indirin ve aÃ§Ä±n.
2. **BaÄŸÄ±mlÄ±lÄ±klar** sekmesine geÃ§in ve **mpv**, **yt-dlp** ile **FFmpeg** iÃ§in
   **Ä°ndir & Kur**'a tÄ±klayÄ±n â€” Ã¼Ã§ ayrÄ± dÃ¼ÄŸme, Ã¼Ã§Ã¼nÃ¼ de kurun. mpv oynatÄ±cÄ±nÄ±n
   kendisidir; diÄŸer ikisi bir akÄ±ÅŸ adresini aÃ§abilmesi iÃ§in gereklidir. AynÄ±
   sekmede **Tema+anime4k Kur** dÃ¼ÄŸmesi ModernZ arayÃ¼zÃ¼nÃ¼ ve Anime4K shader
   setini `%APPDATA%\mpv` iÃ§ine kurar.
3. Yine **BaÄŸÄ±mlÄ±lÄ±klar** sekmesinde **Eklentileri hazÄ±rla / onar**'a tÄ±klayarak
   tarayÄ±cÄ± eklentisini kurun. AynÄ± sekmede eklenti sayfasÄ±nÄ± aÃ§an
   **ðŸ¦Š Firefox Eklentisi** ve Chromium'un ihtiyaÃ§ duyduÄŸu klasÃ¶rÃ¼ aÃ§an
   **Eklenti KlasÃ¶rÃ¼nÃ¼ AÃ§** dÃ¼ÄŸmeleri de var.

**YÃ¶netici hakkÄ± gerekmez.** Her ÅŸey `%APPDATA%\MPVLauncher\` ve
`HKEY_CURRENT_USER` altÄ±na yazÄ±lÄ±r.

> âš ï¸ **`MPVLauncher.exe`'yi baÅŸka bir klasÃ¶re taÅŸÄ±dÄ±ysanÄ±z** uygulamayÄ± bir kez
> aÃ§manÄ±z yeterli. Native messaging host yolunu baÅŸlangÄ±Ã§ta kendisi onarÄ±r,
> yeniden kurulum gerekmez. **Ama tarayÄ±cÄ±nÄ±zÄ± sonra yeniden baÅŸlatÄ±n** â€”
> tarayÄ±cÄ± bu yolu host'u baÅŸlatÄ±rken okur; zaten aÃ§Ä±k olan tarayÄ±cÄ± eski yolu
> kullanmaya devam eder ve dÃ¼ÄŸme hiÃ§bir ÅŸey yapmÄ±yormuÅŸ gibi gÃ¶rÃ¼nÃ¼r. "DÃ¼n
> Ã§alÄ±ÅŸÄ±yordu" demek sorunlarÄ±nÄ±n Ã§oÄŸu bu yÃ¼zden.

> ðŸ’¡ DÃ¼ÄŸme adlarÄ± arayÃ¼z diline gÃ¶re deÄŸiÅŸir; arayÃ¼zÃ¼ TÃ¼rkÃ§e dÄ±ÅŸÄ±nda bir dile
> alÄ±rsanÄ±z Ä°ngilizce gÃ¶rÃ¼nÃ¼rler.

---

## ðŸŽ® NasÄ±l kullanÄ±lÄ±r

Ä°ki yol var ve ikisi de aynÄ± mpv'de sonlanÄ±r.

**ðŸ§© TarayÄ±cÄ±dan** â€” herhangi bir sayfada eklenti simgesine tÄ±klayÄ±n. BulduÄŸu
her ÅŸey **Open in MPV** dÃ¼ÄŸmesiyle listelenir: gerÃ§ek akÄ±ÅŸ, doÄŸrudan medya
dosyalarÄ± ve gÃ¶mÃ¼lÃ¼ oynatÄ±cÄ±lar. Birini seÃ§in, aÃ§Ä±lsÄ±n.

**ðŸ–¥ï¸ Programdan** â€” **OynatÄ±cÄ±** sekmesini aÃ§Ä±n, video adresini yapÄ±ÅŸtÄ±rÄ±n ve
**MPV ile Oynat**'a basÄ±n. Dosyeyi pencereye sÃ¼rÃ¼kleyip bÄ±rakabilir veya
**Dosya SeÃ§**'i kullanabilirsiniz.

Hangisini kullanÄ±rsanÄ±z kullanÄ±n, son 50 oynatma **OynatÄ±cÄ±** sekmesindeki
geÃ§miÅŸte kalÄ±r ve iki yol arasÄ±nda ortaktÄ±r.

---

### ðŸ¦Š Firefox

Ä°mzalÄ± eklentiyi
[addons.mozilla.org](https://addons.mozilla.org/firefox/addon/mpv-launcher/)
adresinden kurun â€” en kolay yol bu ve doÄŸrudan Ã§alÄ±ÅŸÄ±r. Manifest V3 desteÄŸi iÃ§in
Firefox 128+ gerekir.

### ðŸŒ Chrome, Edge, Brave, Opera, Vivaldi, Yandex

Kurulumu yaptÄ±ktan sonra `chrome://extensions` adresini aÃ§Ä±n,
**GeliÅŸtirici modu**'nu etkinleÅŸtirin, **PaketlenmemiÅŸ Ã¶ÄŸe yÃ¼kle**'yi seÃ§in ve ÅŸu
klasÃ¶rÃ¼ gÃ¶sterin:

```
%APPDATA%\MPVLauncher\extensions\chromium
```

Uygulamadaki **Eklenti KlasÃ¶rÃ¼nÃ¼ AÃ§** dÃ¼ÄŸmesi bu klasÃ¶rÃ¼ sizin iÃ§in aÃ§ar.

> ðŸ’¡ Chromium paketlenmemiÅŸ eklenti istediÄŸi iÃ§in eklentinin her aÃ§Ä±lÄ±ÅŸta
> yÃ¼klenmesi gerekir. Kurulum, bunu tarayÄ±cÄ±nÄ±n baÅŸlatma komutuna kullanÄ±cÄ±
> dÃ¼zeyinde geÃ§ersiz kÄ±lmalar yazarak halleder â€” yÃ¶netici hakkÄ± gerekmez ve
> tarayÄ±cÄ±nÄ±n gÃ¼ncelleme sÄ±rasÄ±nda yaptÄ±ÄŸÄ± hiÃ§bir ÅŸey bunu geri almaz. KaldÄ±rma
> yalnÄ±zca bu deÄŸerleri siler.

---

## ðŸ”’ Gizlilik

Eklenti gezdiÄŸiniz sayfalardan adres okur; bu yÃ¼zden sÄ±nÄ±rlarÄ± aÃ§Ä±kÃ§a
belirtmek gerekir:

- ðŸ”‘ **Kimlik bilgileri hiÃ§bir zaman yakalanmaz.** `Cookie` ve `Authorization`
  daha ilk adÄ±mda elenir. Bu yÃ¼zden giriÅŸ gerektiren bir video mpv'de aÃ§Ä±lmaz â€”
  bu bilinÃ§li bir tercihtir.
- ðŸš« **Sayfa betiÄŸi Ã§alÄ±ÅŸtÄ±rÄ±lmaz.** PaketlenmiÅŸ oynatÄ±cÄ± betikleri metin olarak
  ayrÄ±ÅŸtÄ±rÄ±lÄ±r, hiÃ§bir zaman `eval` veya `new Function`'a verilmez.
- ðŸ”’ **YalnÄ±zca `http` ve `https`** oynatÄ±cÄ±ya ulaÅŸÄ±r ve her argÃ¼man dÃ¼zgÃ¼n
  alÄ±ntÄ±lanÄ±r; bÃ¶ylece bir adres ek mpv seÃ§eneÄŸi sÄ±kÄ±ÅŸtÄ±ramaz.
- ðŸ“¦ **ArÅŸiv Ã§Ä±karma sÄ±nÄ±rlÄ±dÄ±r**, indirmeler boyut sÄ±nÄ±rÄ±na tabidir ve imzalÄ±
  medya adreslerinin eriÅŸim anahtarÄ± gÃ¼nlÃ¼ÄŸe yazÄ±lmaz.

AyrÄ±ntÄ±lar:
[`MpvLauncher.Gui/Services/NativeHostService.cs`](MpvLauncher.Gui/Services/NativeHostService.cs)

---

## âŒ¨ï¸ Anime4K kÄ±sayollarÄ± (mpv iÃ§inde)

| TuÅŸ | Ä°ÅŸlev |
| --- | --- |
| `Ctrl+0` | Anime4K'yÄ± kapat |
| `Ctrl+1` | Mod A (hÄ±zlÄ±) |
| `Ctrl+2` | Mod B (yÃ¼ksek kalite) |
| `Ctrl+3` | Mod C (Ã§ok hÄ±zlÄ±) |
| `Ctrl+4` | Mod A+A |
| `Ctrl+5` | Mod B+B |
| `Ctrl+6` | Mod C+A |

---

## ðŸ› ï¸ Kaynaktan derleme

.NET 10 SDK'si gerekir. Node.js yalnÄ±zca eklenti testleri iÃ§in.

```powershell
dotnet build MpvLauncher.Gui\MpvLauncher.Gui.csproj -c Debug
```

Eklenti Ã§alÄ±ÅŸtÄ±rÄ±labilir dosyanÄ±n iÃ§ine gÃ¶mÃ¼lÃ¼dÃ¼r; bu yÃ¼zden bir deÄŸiÅŸikliÄŸin
`%APPDATA%` klasÃ¶rÃ¼ne ulaÅŸmasÄ± iÃ§in C# projesinin yeniden derlenmesi gerekir.
ArdÄ±ndan uygulamada **Eklentileri hazÄ±rla / onar**'Ä± yeniden Ã§alÄ±ÅŸtÄ±rÄ±n.

```powershell
node tools\extension-tests.js   # 147 kontrol - eklenti mantÄ±ÄŸÄ± ve gÃ¼venlik
node tools\locale-tests.js      #  29 kontrol - dil dosyalarÄ± tutarlÄ± kalÄ±r
```

---

## ðŸŒ Diller

**Uygulama (12):** English, TÃ¼rkÃ§e, Deutsch, EspaÃ±ol, Bahasa Indonesia, æ—¥æœ¬èªž,
í•œêµ­ì–´, Polski, PortuguÃªs (Brasil), Ð ÑƒÑÑÐºÐ¸Ð¹, Tiáº¿ng Viá»‡t, ä¸­æ–‡ (ç®€ä½“)

**Eklenti (13):** aynÄ± liste, artÄ± FranÃ§ais.

---

## ðŸ™ TeÅŸekkÃ¼rler

- [mpv](https://mpv.io/) â€” bu projenin var olma sebebi olan oynatÄ±cÄ±
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) â€” akÄ±ÅŸ Ã§Ä±karÄ±mÄ±
- [FFmpeg](https://ffmpeg.org/) â€” ayrÄ±ÅŸtÄ±rma ve dÃ¶nÃ¼ÅŸtÃ¼rme
- [Anime4K](https://github.com/bloc97/Anime4K) â€” gerÃ§ek zamanlÄ± anime yÃ¼kseltme
- [ModernZ](https://github.com/Samillion/ModernZ) â€” mpv iÃ§in modern OSC
- [SharpCompress](https://github.com/adamhathcock/sharpcompress) â€” arÅŸiv Ã§Ä±karma

## ðŸ“„ Lisans

[MIT](LICENSE)
