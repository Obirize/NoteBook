# Notlar (NoteBook)

Windows için Apple Notlar tarzında bir not defteri ve onunla kendi Wi‑Fi'ınız üzerinden eşitlenen bir iPhone uygulaması. Her şey şifreli, hiçbir şey cihazlarınızın dışına çıkmıyor; hesap yok, bulut yok, ödenecek bir şey yok.

[![Son sürüm](https://img.shields.io/github/v/release/Obirize/NoteBook?label=indir&color=e7bb62)](https://github.com/Obirize/NoteBook/releases/latest)
[![İndirme](https://img.shields.io/github/downloads/Obirize/NoteBook/total?color=333337)](https://github.com/Obirize/NoteBook/releases)
[![Lisans: MIT](https://img.shields.io/badge/lisans-MIT-333337)](LICENSE)
![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-333337)
![iPhone](https://img.shields.io/badge/iPhone-Safari%20%E2%86%92%20Ana%20Ekran-333337)

**[⬇ Son kurulum dosyasını indir](https://github.com/Obirize/NoteBook/releases/latest)** · [English](README.md)

![Windows'ta Notlar](docs/screenshot.png)

## Ne yapar

- **Notlar**: başlık, metin, yapılacaklar listesi, fotoğraf ve video. Arama, sabitleme, toplu seçim, 30 günlük *Son silinenler*, TXT içe/dışa aktarma; bilgisayarda da telefonda da 13 arayüz dili.
- **Diskte şifreli.** Notlar AES‑256‑GCM ile mühürlenmiş bir kasada; her fotoğraf ve video kendi anahtarıyla şifrelenmiş ayrı bir dosyada. Bilgisayarda anahtarlar Windows oturumunuzla korunur; uygulama parola sormaz.
- **Sunucusuz iPhone eşitlemesi.** Windows uygulamasının kendisi telefona küçük bir web uygulaması sunar; ana ekrana eklersiniz ve 6 haneli bir kodla eşleştirirsiniz. Sonra notlar ve dosyalar, iki cihaz aynı Wi‑Fi'dayken doğrudan aralarında eşitlenir. Telefon uygulaması Apple Notlar gibi görünür ve çevrimdışı çalışır.
- **Arka planda çalışır.** Pencereyi kapatmak uygulamayı kapatmaz; Notlar bildirim alanında kalır ki telefon eşitlenebilsin. Kurulum sihirbazı Windows ile başlatmayı sunar.
- **Size ait yedekler.** Parolalı yedek her bilgisayarda açılır; geri yükleme üzerine yazmaz, birleştirir.
- **GitHub Releases'tan güncelleme** — uygulamanın ağınız dışında konuştuğu tek yer.

<p align="center"><img src="docs/screenshot-select.png" width="49%" alt="Toplu seçim"> <img src="docs/screenshot-phone-sync.png" width="49%" alt="Telefonla eşitleme penceresi"></p>

## Kurulum

[Releases](https://github.com/Obirize/NoteBook/releases) sayfasından `NoteBook-Setup-<sürüm>.exe` dosyasını indirin. Kurulum kullanıcı bazlıdır (yönetici hakkı istemez), `%LocalAppData%\Programs\NoteBook` içine kurulur, notlarınızı `…\NoteBook\data` içinde tutar ve kaldırırken asla silmez. Masaüstü kısayolu, `.txt` için *Birlikte aç*, sağ tık menüsüne *Yeni not* ve *Windows ile başlat* seçeneklerini sunar.

Kurulum dosyası henüz kod imzalı değildir; ilk indirmede SmartScreen uyarısı çıkabilir ("Daha fazla bilgi → Yine de çalıştır"). Uygulama içi güncellemelerde çıkmaz.

Taşınabilir kullanım da mümkündür: zip'i açın, `Not Defteri.exe` ile `app` klasörünü birlikte tutun; notlar yanlarındaki `data` klasöründe yaşar.

## Telefon (iPhone)

Kenar çubuğunun altındaki telefon düğmesi, her biri QR kodlu üç adımı gösterir:

1. **Güven belgesi.** Bilgisayar kendi sertifika otoritesidir. İlk kod, bir profil indiren sayfayı açar; *Ayarlar → Genel → VPN ve Aygıt Yönetimi*'nden kurun, sonra *Ayarlar → Genel → Hakkında → Sertifika Güven Ayarları*'ndan açın. Sayfadaki *Bağlantıyı sına* düğmesi bunun tamamlandığını söyler. Parmak izi iki tarafta da gösterilir, karşılaştırabilirsiniz.
2. **Ana ekran uygulaması.** İkinci kod uygulamayı Safari'de açar; sayfa iki dokunuşu anlatır: *Paylaş → Ana Ekrana Ekle*. Bundan sonra Notlar'ı ana ekrandan açın (iOS ana ekran uygulamalarına ayrı bir depolama verir).
3. **Eşleştirme kodu.** Ana ekrandaki uygulamada *Eşleştir*'e dokunup bilgisayarda görünen 6 haneli kodu yazın. Her dakika yeni kod gelir; kodlar yalnızca o pencere açıkken vardır.

Sonrasında telefon, evdeki Wi‑Fi'dayken ve bilgisayardaki uygulama çalışırken (tepside gizli olsa da) eşitlenir. Evden uzakta çevrimdışı çalışmaya devam eder, dönünce birleşir. Fotoğraf ve videolar asla yeniden sıkıştırılmaz; uygulama iOS'tan orijinalleri ister. Notta kareler halinde dizilirler; birine dokununca görüntüleyici açılır: *kaydet veya paylaş* (paylaşım menüsündeki *Görüntüyü/Videoyu Kaydet* galeriye koyar) ve *kaldır*; notun *…* menüsü tüm ekleri bir seferde kaydeder. Bilgisayarda *Tüm ekleri kaydet…* (not kartına veya bir eke sağ tık) bir notun ya da işaretli tüm notların dosyalarını bir klasöre yazar.

## Kısayollar (Windows)

| Kısayol | İşlem |
| --- | --- |
| Ctrl+N | Yeni not |
| Ctrl+O | TXT dosyasını yeni not olarak aç |
| Ctrl+Shift+A | Açık nota fotoğraf veya video ekle |
| Ctrl+Shift+L | Bulunulan satırı (veya seçili satırları) yapılacaklar maddesi yap, ya da geri çevir |
| Ctrl+V | Panodaki resmi veya medya dosyalarını ek olarak yapıştır |
| Ctrl+Shift+S | Açık notu şifresiz TXT kopyası olarak kaydet |
| Ctrl+K / Ctrl+F | Arama |
| Ctrl+S | Hemen kaydet / hatalı kaydı yeniden dene |
| Ctrl+Z / Ctrl+Y | Metin düzenlemesini geri al / yinele |
| Delete (yazı alanı dışında) | Açık veya işaretli notları son silinenlere taşı; orada kalıcı sil (onaylı) |
| Shift+Delete | Kalıcı sil (onaylı) |
| Ctrl+A (seçim modunda) | Tümünü seç |
| Esc | Seçim modundan çık |

## Şifreleme nasıl çalışıyor

**Bilgisayarda.** Not içerikleri, başlıklar, tarihler ve eski dosya arşivleri AES‑256‑GCM ile şifrelenir. Rastgele 256 bitlik içerik anahtarı Windows DPAPI (`CurrentUser`) ile sarılarak kasaya yazılır; her kayıtta yeni nonce kullanılır, her değişiklik doğrulamada yakalanır. Her ek `data/attachments/<id>.bin` olarak, kendi rastgele anahtarıyla 1 MB'lık AES‑256‑GCM parçaları halinde yazılır; anahtar ve dosya bilgileri yalnızca şifreli not defterinin içindedir ve her parça dosya başlığını, ek kimliğini, sırasını ve "son parça" işaretini doğrular — dosyalar kesilemez, sıralanamaz, değiştirilemez. Video oynatılırken oynatıcıya geçici klasörde çözülmüş bir kopya verilir; görüntüleyici kapanınca silinir.

Ayrı bir uygulama parolası veya boşta kilidi yoktur: notları Windows oturumunuz korur. Amaç, **dosyaları tek başına kopyalayan birinin okuyamamasıdır**. Aynı Windows hesabında çalışan bir program ya da açık oturumunuza erişen biri notları açabilir; bu, yönetici yetkili zararlı yazılıma, klavye kaydına veya bellek dökümüne karşı bir savunma değildir.

**Cihazlar arasında.** Telefon ile bilgisayar 256 bitlik bir eşitleme anahtarını paylaşır; anahtar bir kez, TLS üzerinden, eşleştirme kodu karşılığında gider. İki taraf bundan bir kimlik doğrulama anahtarı (her bağlantıda karşılıklı HMAC sınaması) ve bir içerik anahtarı (AES‑256‑GCM) türetir. Her not bu içerik anahtarıyla şifreli olarak taşınır ve telefonda da öyle saklanır; ekler zaten şifreli dosyalar olarak, bayt bayt aktarılır. Sunucu TCP 47831 (HTTPS + WebSocket) ve 47832 (belgeyi veren düz kurulum sayfası) bağlantı noktalarını dinler, yalnızca yerel ağdaki adreslere yanıt verir ve yalnızca anahtarı kanıtlayan cihazla konuşur. Birleştirmede notun yüksek revizyonu kazanır; iki cihaz aynı revizyonu düzenlemişse yeni olan kalır, diğeri "çakışma kopyası" olarak saklanır. Kalıcı silmeler 180 gün hatırlanır; telefon silinmiş bir notu geri getiremez.

Kaynaklar: [DataProtectionScope](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope), [AesGcm](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm). Telemetri yok. Bağımsız denetimden geçmemiştir.

## Yapılacaklar listesi

Liste düğmesi (bilgisayarda Ctrl+Shift+L, telefonda üst çubuktaki liste simgesi) bulunulan satırı yuvarlak onay kutulu bir maddeye çevirir; Enter listeyi sürdürür, boş maddede Enter listeyi bitirir, madde başında Backspace onu düz metne çevirir, yuvarlağa dokunmak maddeyi tamamlanmış (üstü çizili) yapar. Maddeler `○` veya `●` ile başlayan düz satırlar olarak saklanır; bu yüzden başka her metin gibi eşitlenir, aranır ve dışa aktarılır.

## Dosyalar, yedek ve kurtarma

- `data/notes.vault` — şifreli not defteri; `data/notes.vault.bak` — önceki kayıt.
- `data/attachments/` — şifreli fotoğraf ve videolar, her ek için bir dosya.
- `data/sync/` — sertifikalar, eşitleme anahtarı (DPAPI ile korunur) ve eşleşmiş telefonların listesi.
- `data/settings.json` — dil ve küçük tercihler.

**Yedek** (kenar çubuğundaki kutu simgesi) → *Yedek al…* seçtiğiniz parolayla korunan bir `.vault` dosyası yazar (PBKDF2 600k + AES‑256‑GCM); ek varsa yanına `<ad>.vault.files` klasörü de yazılır, ikisini birlikte saklayın. Dosya o parolayla her bilgisayarda açılır. *Yedekten geri yükle…* birleştirir: yerelde olmayan notlar eklenir, her notun daha yeni revizyonu kazanır, eksik dosyalar kopyalanır, hiçbir şey silinmez.

Ana dosya açılamazsa uygulama önceki kaydı önerir; hasarlı dosyalar `.damaged-…` olarak korunur, sessizce boş notlarla değiştirilmez. Son silinenlerdeki notlar 30 gün sonra ek dosyalarıyla birlikte temizlenir.

## Diller

Windows uygulaması Windows görüntü dilini izler ve küre düğmesinden 13 dil sunar: Türkçe, İngilizce, İspanyolca, Çince, Hintçe, Arapça (sağdan sola), Portekizce, Rusça, Japonca, Almanca, Fransızca, Endonezce, Korece. Metinler `src/Notlar/Languages/<kod>.json` dosyalarındadır; test, her dilde her anahtarın bulunduğunu doğrular. Telefon uygulaması telefonun dilini izler; aynı 13 dil (`src/Notlar/Web/lang.js`).

<p align="center"><img src="docs/screenshot-arabic.png" width="70%" alt="Arapça, sağdan sola"></p>

## Geliştirme

Windows ve .NET 10 SDK; telefon testi için Node.js. Üçüncü taraf paket yok: HTTPS/WebSocket sunucusu, sertifika otoritesi ve QR üretici kaynağın parçasıdır.

- `src/Notlar/` — WPF uygulaması. `Sync/` sunucu, protokol, sertifika ve QR kodunu; `Web/` telefon uygulamasını (düz JavaScript, WebCrypto, IndexedDB) içerir.
- `build.cmd` — kendi kendine yeten x64 derlemeyi `app/` içine yayınlar ve kök başlatıcıyı derler.
- `test.cmd` — geçici verilerle kontrolleri çalıştırır: şifreleme ve tahrif tespiti, ekler, yedekler, silme politikası, toplu işlemler, tepsi davranışı, arayüz yerleşimi ve telefonu canlandıran bir Node betiği (eşleştirme kodu, iki yönlü eşitleme, çakışmalar, bayt bayt aynı dosyalar).
- `build-setup.cmd` — [Inno Setup 6](https://jrsoftware.org/isdl.php) ile kurulum dosyasını `dist/` içine üretir.

Yayın: bir sürüm etiketi gönderin; `.github/workflows/release.yml` kurulum dosyasını derler, `.sha256` özetini yazar ve ikisini bir GitHub Release'e ekler. Çalışan uygulamalar birkaç saat içinde güncellemeyi sunar.

```bash
git tag v1.6.0
git push --tags
```

## Yol haritası

- Android (aynı web uygulaması Chrome'da; sertifika adımı farklı).
- Ev dışından eşitleme — bugün telefon evdeki Wi‑Fi'da eşitlenir; seçenekler bilgisayarda kendi WireGuard'ınız ya da içeriği göremeyen, yalnızca taşıyan bir aracı.
- Kurulum dosyası için kod imzası.

## Lisans

MIT — bkz. [LICENSE](LICENSE).
