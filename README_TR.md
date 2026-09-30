# MPVLauncher

> **Bu proje yapay zekâ desteğiyle yazıldı.** Uygulamanın tamamı - WPF masaüstü
> uygulaması, tarayıcı eklentisi, dokümantasyon ve testler - yapay zekâ kodlama
> desteğiyle üretildi ve bir insan tarafından gözden geçirilip düzeltildi.
> Kodu okumaya, çalıştırmaya ve değiştirmeye açıktır.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Version](https://img.shields.io/badge/version-1.9.3-blue.svg)](extension/manifest.json)

**Diğer dil:** [English](README.md)

---

## Ne olduğu

MPVLauncher, [mpv](https://mpv.io/) için bir masaüstü başlatıcısıdır; yanında
da web'deki videoyu doğrudan yerel oynatıcınıza gönderen bir tarayıcı
eklentisi bulunur.

Uygulama mpv'nin gerçekten ihtiyaç duyduğu üç aracı (mpv, yt-dlp, FFmpeg)
indirip yönetir, hem Chromium hem Firefox için tarayıcı eklentisini kurar,
temalar ve Anime4K yükseltme desteği sunar. Python yoktur, harici bir çalışma
zamanı yoktur ve sisteme hiçbir şey kurulmaz: her şey kendi kullanıcı
hesabınızın altında kalır.

Normalde bir tarayıcıdan video açmak için indirme aracı, betik ya da sayfaya
özel bir yöntem gerekir. Bu projenin amacı bunu tek tıkla hâle getirmek.

---

## Özellikler

### Masaüstü uygulaması (WPF, .NET 10, Windows 10/11)

* **Tek tıkla araç kurulumu.** mpv, yt-dlp ve FFmpeg'i kendi GitHub sürüm
  yayınlarından indirir, yarım kalmış bir kurulum bırakmaz ve yt-dlp'yi yerinde
  günceller.
* **Tarayıcı entegrasyonu kurucusu.** Chrome, Edge, Brave, Opera, Vivaldi,
  Yandex ve Firefox için native messaging host'u kaydeder; tarayıcı başlatma
  komutlarının kullanıcı düzeyi geçersiz kılmalarını yazarak eklentinin normal
  pencerede de her açılışta yüklenmesini sağlar; uygulama klasörü taşındıktan
  sonra host yolunu onarır.
* **Eklenti onarımı ve kaldırma.** Her dosyayı ve kayıt defteri girdisini
  yeniden yazan tam onarım; ve yalnızca kurulan şeyi, başka hiçbir şeyi
  etkilemeden kaldıran kaldırma - Firefox `user.js` dosyanız ve bir kuruluşun
  `policies.json` dosyası olduğu gibi bırakılır.
* **Temalar ve canlı görünüm düzenleyici.** Dört hazır tema; ayrı köşe
  yuvarlaklıkları, bağımsız saydamlık, buzlu cam bulanıklığı, gölge ayarları,
  renk paleti ve özel arka plan görseli.
* **Anime4K ve ModernZ.** Anime4K GLSL shader setini ve ModernZ ekran üstü
  kontrol arayüzünü `%APPDATA%\mpv` içine kurar.
* **Oynatma geçmişi.** Son 50 kayıt; tarayıcı eklentisiyle paylaşılır.
* **Arka plan önbellekleme.** Uzak arka plan bir kez alınır ve tekrar kullanılır,
  böylece açılış ağa bağlı kalmaz.
* **12 dilde arayüz.**

### Tarayıcı eklentisi (Chromium ve Firefox, Manifest V3)

* **Bağımsız iki algılama katmanı.** `webRequest` dinleyicisi sayfanın yaptığı
  her medya isteğini görür - DOM'da hiçbir düğümün göstermediği istekler dâhil -
  bir içerik betiği ise henüz çekilmemiş bir akışı bulmak için işaretleme, oynatıcı
  yapılandırması ve tembel yüklenen öznitelikleri tarar. Sonuçlar birleştirilip
  tekrarsızlaştırılır.
* **Yaygın durumları yakalar.** HLS/m3u8 ve DASH manifestleri, doğrudan medya
  dosyaları, `<video>` / `<source>` işaretlemesi, `object` ve `embed`
  oynatıcıları, JSON-LD ve oynatıcı yapılandırma nesneleri, blob ve MSE
  adresleri, paketlenmiş (Dean Edwards) satır içi betikler.
* **iframe algılama.** Başka bir siteden gelen çapraz kökenli oynatıcı
  çerçeveleri bulunur ve listelenir, böylece gömülü videolar doğrudan açılabilir.
* **Filtre paneli.** Akışları, doğrudan dosyaları ve iframe'leri ayrı ayrı
  açıp kapatma, en küçük dosya boyutu belirleme ve gizlenecek sunucu kara listesi.
* **Doğrulama sayfası farkındalığı.** reCAPTCHA veya Cloudflare çerçevesi
  tanınır ve "boş sayfa" yerine öyle bildirilir.
* **Dürüst tanılar.** Durum satırı sonuçları kaynağına göre ayırır; böylece
  "hiçbir şey bulunamadı" ile "yakalama çalışmıyor" birbirinden ayrılır.
* **İstek başlıklarını iletir.** Sayfanın gerçekten kullandığı `User-Agent` ve
  `Referer` mpv'ye aktarılır. Kimlik bilgileri hiçbir zaman yakalanmaz -
  bkz. [Gizlilik ve güvenlik](#gizlilik-ve-güvenlik).
* **13 dilde arayüz.**

---

## Öneri: Chromium eklentisini kullanın

**Chromium sürümü şu anda iki seçenek arasında daha güvenilir olanı.** Firefox
da çalışıyor, ancak Firefox'un Manifest V3 eklentisi, tarayıcı boştayken
sonlandırılan geçici (non-persistent) bir olay sayfası olarak çalışır; bu da
bellekteki yakalama durumunu kaybettirir ve algılamayı sayfa yüklemeleri arasında
tutarsız hale getirir. Chromium'un servis worker'ı daha uzun yaşar, bu yüzden
yakalamalar daha güvenilir birikir.

İkisi de desteklenir ve ikisi de aynı kaynak ağacından gelir; fark bir özellik
farkı değil, bir platform davranışıdır. Seçme şansınız varsa Chrome, Edge ya da
Brave tercih edin.

---

## Kurulum

### Uygulama

1. Başlatıcıyı indirin ve çalıştırın.
2. **Bağımlılıklar** sekmesini açın ve mpv, yt-dlp ile FFmpeg için
   **İndir ve Kur** düğmesine tıklayın. Başlamak için yalnızca mpv yeterlidir.
3. **Ayarlar → Tarayıcı eklentisi entegrasyonu** bölümünü açın ve
   **Eklentileri kur / onar** düğmesine tıklayın.

Yönetici hakkı gerekmez. Her şey `%APPDATA%\MPVLauncher\` ve
`HKEY_CURRENT_USER` altına yazılır.

### Chromium (Chrome, Edge, Brave, Opera, Vivaldi, Yandex)

Kurulumu yaptıktan sonra `chrome://extensions` adresini açın, **Geliştirici
modu**nu etkinleştirin, **Paketlenmemiş öğe yükle** seçeneğini seçin ve şu klasörü
gösterin:

```
%APPDATA%\MPVLauncher\extensions\chromium
```

Uygulamadaki **Eklenti Klasörü** düğmesi bu klasörü sizin için açar.

> Chrome 137 ve sonrası, `DisableLoadExtensionCommandLineSwitch` ilkesi
> ayarlanmadıkça `--load-extension` argümanını yok sayar. Kurulum, bu ilkeyi
> sizin kullanıcı hesabınız için kendisi yazar.
>
> Eklentinin **her** açılışta tarayıcı tarafından yüklenmesi gerekir ve
> Chromium'un bunu kabul ettiği tek yol bir komut satırı argümanıdır. Tarayıcılar
> bu komutu `HKEY_LOCAL_MACHINE` altına kaydeder; kullanıcı düzeyinde çalışan bir
> uygulama orayı düzenleyemez. Bu yüzden kurulum, aynı anahtarları
> `HKEY_CURRENT_USER` altına yazar - Windows ikisini birleştirir, yönetici hakkı
> gerekmez ve tarayıcının güncelleme sırasında yaptığı hiçbir şey bunu geri
> almaz. Kaldırma yalnızca bu değerleri siler, makine kayıtlarına dokunmaz.

### Firefox

İmzalı eklentiyi
[addons.mozilla.org](https://addons.mozilla.org/firefox/addon/mpv-launcher/)
adresinden kurun ya da `about:debugging` → **Bu Firefox** → **Geçici Eklenti
Yükle** yoluyla `extensions\firefox` klasörünü yükleyin.

Firefox, Manifest V3 desteği için 128 veya üzeri sürüm gerektirir.

---

## Gizlilik ve güvenlik

Bu proje gezdiğiniz sayfalardan adres alıyor; bu yüzden sınırları açıkça
belirtmek gerekir.

* **Kimlik bilgileri hiçbir zaman yakalanmaz.** Eklentinin `webRequest`
  katmanı yalnızca `User-Agent`, `Referer` ve `Origin` başlıklarını tutar;
  `Cookie` ve `Authorization` elenir. Giriş yapmanıza yarayacak hiçbir şey
  saklanmaz, iletilmez veya diske yazılmaz. Bu yüzden giriş gerektiren bir video
  mpv'de açılmaz.
* **Sizin adınıza hiçbir şey uydurulmaz.** mpv başlatıldığında `Referer`
  tarayıcının gerçekten gönderdiği değerdir. Önceki sürümler bunun yerine medya
  sunucusunun kendi kökenini koyuyordu; bu, yt-dlp'nin bazı adresleri
  (`Unsupported URL`) doğrudan reddetmesine yol açtığı için kaldırıldı.
* **Sayfa betiği çalıştırılmaz.** Paketlenmiş oynatıcı betikleri, paketleyicinin
  sözlüğü metin olarak ayrıştırılarak okunur. Bunlar hiçbir zaman `eval` veya
  `new Function`'a verilmez; aksi halde düşmanca bir sayfaya eklentinin kendi
  API'si verilirdi.
* **Native host genel amaçlı bir başlatıcı değildir.** Yalnızca `http` ve
  `https` kabul eder. `file://` ya da `\\sunucu\paylasim\...` bir değer, mpv'nin
  HTTP istemcisini uzak bir sunucuya NTLM yanıtı vermeye itebilirdi.
* **Argümanlar argüman olarak geçilir.** Her mpv argümanı tek bir metne
  yapıştırılmak yerine `CommandLineToArgvW` kurallarına göre tokenlaştırılıp
  alıntılanır; böylece bir adres `--script` gibi ek mpv seçenekleri
  sıkıştıramaz.
* **İndirilen arşivler sınırlandırılır.** Çıkarma klasörünün dışına yazılacak
  girdiler ve bağlantı (link) girdileri, herhangi bir şey yazılmadan önce
  reddedilir.
* **İndirmeler sınırlıdır.** Araç indirmeleri boyut sınırına tabidir; mpv'nin
  çalıştırdığı bir Lua betiği içeren ModernZ teması ise https ve GitHub
  sunucularıyla sınırlıdır ve yönlendirmeden sonra yeniden denetlenir.
* **İmzalı medya adresleri günlüğe yazılmaz.** Native host günlüğü adresin
  sunucu ve yol bilgisini tutar, erişim anahtarının bulunduğu sorgu dizesini
  ise değiştirir; günlük döndürülerek büyümesi sınırlanır.
* **Çağıran taraf tarayıcı tarafından doğrulanır.** İki tarayıcı da host'u
  başlatmadan önce kendi izin listelerini (`allowed_origins` /
  `allowed_extensions`) kontrol eder.

Uygulama arayüzüne kendinizin yazdığınız yerel dosyalar normal şekilde
başlatılır; yukarıdaki kısıtlamalar bir web sayfasından gelen değerler için
geçerlidir.

---

## Diller

**Uygulama (12):** English, Türkçe, Deutsch, Español, Bahasa Indonesia,
日本語, 한국어, Polski, Português (Brasil), Русский, Tiếng Việt, 中文 (简体)

**Eklenti (13):** aynı liste, artı Français.

---

## Anime4K kısayolları (mpv içinde)

| Tuş | İşlev |
| --- | --- |
| `Ctrl+0` | Anime4K'yı kapat |
| `Ctrl+1` | Mod A (hızlı) |
| `Ctrl+2` | Mod B (yüksek kalite) |
| `Ctrl+3` | Mod C (çok hızlı) |
| `Ctrl+4` | Mod A+A |
| `Ctrl+5` | Mod B+B |
| `Ctrl+6` | Mod C+A |

---

## Kaynaktan derleme

.NET 10 SDK'si ve Node.js gerekir (Node yalnızca eklenti testleri için).

```powershell
dotnet build MpvLauncher.Gui\MpvLauncher.Gui.csproj -c Debug
```

`extension/` içindeki eklenti dosyaları çalıştırılabilir dosyanın içine
gömülüdür; bu yüzden bir değişikliğin `%APPDATA%` klasörüne ulaşması için C#
projesinin yeniden derlenmesi gerekir. Ardından uygulamada **Eklentileri kur /
onar** düğmesini yeniden çalıştırarak dosyaları tarayıcı klasörlerine
kopyalayın.

### Testler

```powershell
node tools\extension-tests.js   # 147 kontrol - eklenti mantığı ve güvenlik
node tools\locale-tests.js      #  29 kontrol - yerelleştirme dosyaları tutarlı kalır
```

---

## Depo düzeni

```
MpvLauncher.Gui/     WPF uygulaması (.NET 10)
  Services/          kurulum, native host, indirmeler, temalar, i18n
extension/           tarayıcı eklentisi, iki tarayıcı için ortak
locales/             12 uygulama dil dosyası
themes/              tema tanımları
tools/               eklenti ve yerelleştirme testleri
```

---

## Teşekkürler

* [mpv](https://mpv.io/) - bu projenin var olma sebebi olan oynatıcı.
* [yt-dlp](https://github.com/yt-dlp/yt-dlp) - akış çıkarımı.
* [FFmpeg](https://ffmpeg.org/) - ayrıştırma ve dönüştürme.
* [Anime4K](https://github.com/bloc97/Anime4K) - gerçek zamanlı anime
  yükseltme.
* [ModernZ](https://github.com/Samillion/ModernZ) - mpv için modern OSC.
* [SharpCompress](https://github.com/adamhathcock/sharpcompress) - araç kurulumu
  sırasında arşiv çıkarma.

Algılama yaklaşımı, bir `webRequest` dinleyicisi ile DOM taramasını eşleştiren
yaygın tekniği izler; bu, mevcut hiçbir eklentiden kopyalanmadan bu proje için
bağımsız olarak uygulanmıştır.

---

## Lisans

[MIT](LICENSE). Tam metin için [LICENSE](LICENSE) dosyasına bakın.
