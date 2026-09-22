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

- **Notlar**: başlık, metin, yapılacaklar listesi, fotoğraf ve video. Arama, sabitleme, toplu seçim, göz önünden kaldırmak istediğiniz notlar için *Arşiv*, 30 günlük *Son silinenler*, TXT, Markdown, HTML, RTF ve Word dosyalarını not olarak açma, notu TXT ya da Markdown olarak kaydetme; bilgisayarda da telefonda da 13 arayüz dili.
- **Diskte şifreli.** Notlar AES‑256‑GCM ile mühürlenmiş bir kasada; her fotoğraf ve video kendi anahtarıyla şifrelenmiş ayrı bir dosyada. Bilgisayarda anahtarlar Windows oturumunuzla korunur; uygulama parola sormaz.
- **Sunucusuz iPhone eşitlemesi.** Windows uygulamasının kendisi telefona küçük bir web uygulaması sunar; ana ekrana eklersiniz ve 6 haneli bir kodla eşleştirirsiniz. Sonra notlar ve dosyalar, iki cihaz aynı Wi‑Fi'dayken doğrudan aralarında eşitlenir. Telefon uygulaması Apple Notlar gibi görünür ve çevrimdışı çalışır.
- **Arka planda çalışır.** Pencereyi kapatmak uygulamayı kapatmaz; Notlar bildirim alanında kalır ki telefon eşitlenebilsin. Kurulum sihirbazı Windows ile başlatmayı sunar.
- **Size ait yedekler.** Parolalı yedek her bilgisayarda açılır; geri yükleme üzerine yazmaz, birleştirir.
- **GitHub Releases'tan güncelleme** — uygulamanın ağınız dışında konuştuğu tek yer.

<p align="center"><img src="docs/screenshot-select.png" width="49%" alt="Toplu seçim"> <img src="docs/screenshot-phone-sync.png" width="49%" alt="Telefonla eşitleme penceresi"></p>

## Kurulum

[Releases](https://github.com/Obirize/NoteBook/releases) sayfasından `NoteBook-Setup-<sürüm>.exe` dosyasını indirin. Kurulum kullanıcı bazlıdır (yönetici hakkı istemez), `%LocalAppData%\Programs\NoteBook` içine kurulur, notlarınızı `…\NoteBook\data` içinde tutar ve kaldırırken asla silmez. Masaüstü kısayolu, `.txt`, `.md`, `.markdown`, `.text` ve `.log` için *Birlikte aç*, sağ tık menüsüne *Yeni not* ve *Windows ile başlat* seçeneklerini sunar.

Kurulum dosyası henüz kod imzalı değildir; ilk indirmede SmartScreen uyarısı çıkabilir ("Daha fazla bilgi → Yine de çalıştır"). Uygulama içi güncellemelerde çıkmaz.

Taşınabilir kullanım da mümkündür: zip'i açın, `Not Defteri.exe` ile `app` klasörünü birlikte tutun; notlar yanlarındaki `data` klasöründe yaşar.

## Telefon (iPhone)

Kenar çubuğunun altındaki telefon düğmesi, her biri QR kodlu üç adımı gösterir:

1. **Güven belgesi.** Bilgisayar kendi sertifika otoritesidir. İlk kod, bir profil indiren sayfayı açar; *Ayarlar → Genel → VPN ve Aygıt Yönetimi*'nden kurun, sonra *Ayarlar → Genel → Hakkında → Sertifika Güven Ayarları*'ndan açın. Sayfadaki *Bağlantıyı sına* düğmesi bunun tamamlandığını söyler. Parmak izi iki tarafta da gösterilir, karşılaştırabilirsiniz.
2. **Ana ekran uygulaması.** İkinci kod uygulamayı Safari'de açar; sayfa iki dokunuşu anlatır: *Paylaş → Ana Ekrana Ekle*. Bundan sonra Notlar'ı ana ekrandan açın (iOS ana ekran uygulamalarına ayrı bir depolama verir).
3. **Eşleştirme kodu.** Ana ekrandaki uygulamada *Eşleştir*'e dokunup bilgisayarda görünen 6 haneli kodu yazın. Her dakika yeni kod gelir; kodlar yalnızca o pencere açıkken vardır.

Sonrasında telefon, evdeki Wi‑Fi'dayken ve bilgisayardaki uygulama çalışırken (tepside gizli olsa da) eşitlenir. Evden uzakta çevrimdışı çalışmaya devam eder, dönünce birleşir. Fotoğraf ve videolar asla yeniden sıkıştırılmaz; uygulama iOS'tan orijinalleri ister. Notta kareler halinde dizilirler (videonun ilk karesi görünür: dosyayı hangi taraf açabiliyorsa küçük bir resmi bir kez o yapar ve resim notla birlikte taşınır; bilgisayarın oynatamadığı bir iPhone HEVC videosu bile orada ön izleme alır); birine dokununca görüntüleyici açılır: *kaydet veya paylaş* (paylaşım menüsündeki *Görüntüyü/Videoyu Kaydet* galeriye koyar) ve *kaldır*. Notun *…* menüsünde *Fotoğraf ve Videoları Seç* var: istediklerinizi işaretleyin, *Kaydet (n)* hepsini birden paylaşım menüsüne gönderir, *Kaldır (n)* nottan çıkarır; *Tümünü kaydet* de orada. Üstteki paylaş düğmesi notun tamamını (metin ve medya) paylaşır. Bilgisayarda bir karenin üstüne gelince onay yuvarlağı belirir (Ctrl+tık ya da Boşluk da olur); işaretli kare varken bir çubuk *Seçilenleri kaydet…* ve *Seçilenleri kaldır* sunar; *Tüm ekleri kaydet…* (not kartına veya bir eke sağ tık) bir notun ya da işaretli tüm notların dosyalarını bir klasöre yazar.

Not ekranı Notlar uygulamasını izler: üstte geri, paylaş ve *…*; altta liste, kamera, sabitle ve yeni not; yazarken alt çubuk klavyenin üstünde durur.

## Bağlantı nasıl ayakta kalıyor

Telefon, sesini duymadığı bir bağlantıya güvenmez. Her deneme süreyle sınırlıdır (açılmak için 10 sn, "hoş geldin" için 25 sn); uygulama ekrandayken 20 sn'de bir küçük bir "orada mısın?" gönderir; bir aradan sonra uygulamaya döndüğünüzde bir kez daha sorar ve 4 sn içinde cevap gelmezse kendisi yeni bağlantı kurar (45 sn'den uzun ayrı kaldıysa sormaz bile). 5 saniyelik bir sayaç, iOS hiç haber vermese de uyutulmuş sayfayı fark eder. Bilgisayar önce adıyla, üç saniye sonra adresiyle denenir; en son hangisi cevap verdiyse bir dahaki sefer önce o denenir. Bilgisayar tarafında bir dakikadır ses çıkarmayan telefon düşürülür, aynı telefon yeniden gelince eski oturumunun yerini alır ve her oturumun bitişi tarihli bir günlüğe yazılır (`data/sync/sync-log.txt`; eşitleme penceresinde de *Günlüğü kopyala* düğmesiyle görünür) — telefonun önceki bağlantısı hakkında bildirdikleriyle birlikte ("hidden 28800 s, prev no-pong"). Bilgisayarın belgesi adreslerini izler ve program açıkken kendini yeniler; telefondaki profil hiç değişmez.

## Telefon bağlanmazsa

Bilgisayardaki eşitleme penceresinde tarihli **Son bağlantılar** listesi var: telefonun bilgisayara ulaşıp ulaşmadığı, hangi ad ya da adresi kullandığı, güven belgesini kabul edip etmediği, bir oturumun neden bittiği ve eşleştirme kodunun neden reddedildiği. Telefonda Safari'de iki hızlı deneme:

- `http://<pc>.local:47832/` (1. adımın altındaki adres; pencere `http://192.168.1.8:47832/` gibi IP'li halini de gösterir). Açılmıyorsa telefon bilgisayara ağ üzerinden ulaşamıyor: ikisi de aynı Wi‑Fi adında olmalı, modemin "istemci ayırma / AP isolation" ayarı kapalı olmalı.
- `https://<pc>.local:47831/start`. İlki açılıp bu açılmıyorsa telefon bilgisayarın güven belgesine artık güvenmiyor — bilgisayar da birkaç başarısız denemeden sonra bunu söyler. Telefonda: Ayarlar → Genel → Hakkında → Sertifika Güven Ayarları → Notlar belgesinin anahtarını açın (satır yoksa 1. adımı yeniden yapın). Bu bir iOS güncellemesinden ya da profilin silinmesinden sonra olabilir; normal bir kapatıp açmada olmaz.

Ana ekrandaki uygulama bilgisayar güncellemesinden sonra bozuk ya da boş bir ekran gösterirse çevrimdışı kopyasını atıp kendini bir kez yeniler; yine olmazsa simgeyi silip `/start` adresinden yeniden ekleyin.

## Kısayollar (Windows)

| Kısayol | İşlem |
| --- | --- |
| Ctrl+N | Yeni not |
| Ctrl+O | Metin dosyalarını (TXT, Markdown, HTML, RTF, DOCX, …) yeni not olarak aç |
| Ctrl+Shift+A | Açık nota fotoğraf veya video ekle |
| Ctrl+Shift+L | Bulunulan satırı (veya seçili satırları) yapılacaklar maddesi yap, ya da geri çevir |
| Ctrl+E | Açık ya da seçili notları arşivle, arşivdeyse geri getir |
| Ctrl+V | Panodaki resmi veya medya dosyalarını ek olarak yapıştır |
| Ctrl+Shift+S | Açık notu şifresiz TXT ya da Markdown kopyası olarak kaydet |
| Ctrl+K / Ctrl+F | Arama |
| Ctrl+S | Hemen kaydet / hatalı kaydı yeniden dene |
| Ctrl+Z / Ctrl+Y | Metin düzenlemesini geri al / yinele |
| Delete (yazı alanı dışında) | Açık veya işaretli notları son silinenlere taşı; orada kalıcı sil (onaylı) |
| Shift+Delete | Kalıcı sil (onaylı) |
| Ctrl+A (seçim modunda) | Tümünü seç |
| Esc | Seçim modundan çık |

## Şifreleme nasıl çalışıyor

**Bilgisayarda.** Not içerikleri, başlıklar ve tarihler AES‑256‑GCM ile şifrelenir. Rastgele 256 bitlik içerik anahtarı Windows DPAPI (`CurrentUser`) ile sarılarak kasaya yazılır; her kayıtta yeni nonce kullanılır, her değişiklik doğrulamada yakalanır. Her ek `data/attachments/<id>.bin` olarak, kendi rastgele anahtarıyla 1 MB'lık AES‑256‑GCM parçaları halinde yazılır; anahtar ve dosya bilgileri yalnızca şifreli not defterinin içindedir ve her parça dosya başlığını, ek kimliğini, sırasını ve "son parça" işaretini doğrular — dosyalar kesilemez, sıralanamaz, değiştirilemez. Video oynatılırken oynatıcıya geçici klasörde çözülmüş bir kopya verilir; görüntüleyici kapanınca silinir.

Ayrı bir uygulama parolası veya boşta kilidi yoktur: notları Windows oturumunuz korur. Amaç, **dosyaları tek başına kopyalayan birinin okuyamamasıdır**. Aynı Windows hesabında çalışan bir program ya da açık oturumunuza erişen biri notları açabilir; bu, yönetici yetkili zararlı yazılıma, klavye kaydına veya bellek dökümüne karşı bir savunma değildir.

**Cihazlar arasında.** Telefon ile bilgisayar 256 bitlik bir eşitleme anahtarını paylaşır; anahtar bir kez, TLS üzerinden, eşleştirme kodu karşılığında gider. İki taraf bundan bir kimlik doğrulama anahtarı (her bağlantıda karşılıklı HMAC sınaması) ve bir içerik anahtarı (AES‑256‑GCM) türetir. Her not bu içerik anahtarıyla şifreli olarak taşınır ve telefonda da öyle saklanır; ekler zaten şifreli dosyalar olarak, bayt bayt aktarılır. Sunucu TCP 47831 (HTTPS + WebSocket) ve 47832 (belgeyi veren düz kurulum sayfası) bağlantı noktalarını dinler, yalnızca yerel ağdaki adreslere yanıt verir ve yalnızca anahtarı kanıtlayan cihazla konuşur. Birleştirmede notun yüksek revizyonu kazanır; iki cihaz aynı revizyonu düzenlemişse yeni olan kalır, diğeri "çakışma kopyası" olarak saklanır. Kalıcı silmeler 180 gün hatırlanır; telefon silinmiş bir notu geri getiremez.

Kaynaklar: [DataProtectionScope](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope), [AesGcm](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm). Telemetri yok. Bağımsız denetimden geçmemiştir.

## Arşiv

Listenin üstündeki klasör satırında üç seçenek var: *Tüm notlar*, *Arşiv* ve *Son silinenler*. Not araç çubuğundaki klasör düğmesi (Ctrl+E), kartın sağ tık menüsü ya da seçim modundaki toplu çubuk notları arşive taşır; aynı düğme arşivde geri getirir. Arşivdeki notlar düzenlenebilir ve arşiv içinde aranabilir, her değişiklik gibi telefona eşitlenir ve hiçbir zaman silinmez. Telefonda uygulama Notlar gibi açılır: bir *Klasörler* ekranı (*Notlar*, *Arşiv*, *Son Silinenler*, sayılarıyla) tek bir kart halindeki listeye götürür; alt çubukta not sayısı (notlar güncel olana kadar bağlantının ne yaptığı) yazar; bir satırı sola kaydırınca paylaş, taşı ve sil (çöpte geri yükle ve kalıcı sil) çıkar, uzun kaydırma sonuncuyu çalıştırır. "…" menüsünde *Notları Seç*, *Şimdi eşitle* ve *Yeniden eşleştir* var; çöpte onun yerine *Düzenle* durur. *Son silinenler*'den geri yüklenen not her zaman *Tüm notlar*'a döner.

## Yapılacaklar listesi

Liste düğmesi (bilgisayarda Ctrl+Shift+L, telefonda üst çubuktaki liste simgesi) bulunulan satırı yuvarlak onay kutulu bir maddeye çevirir; Enter listeyi sürdürür, boş maddede Enter listeyi bitirir, madde başında Backspace onu düz metne çevirir, yuvarlağa dokunmak maddeyi tamamlanmış (üstü çizili) yapar. Maddeler `○` veya `●` ile başlayan düz satırlar olarak saklanır; bu yüzden başka her metin gibi eşitlenir, aranır ve dışa aktarılır.

## Metin dosyalarını açma ve kaydetme

Ctrl+O, *Metin dosyası aç* menüsü, pencereye sürükleyip bırakma veya Gezgin'de *Birlikte aç* bir dosyayı yeni nota çevirir (birden çok dosya birden çok not olur). Asıl dosyaya dokunulmaz. Desteklenenler:

| Tür | Uzantılar | Nota ne geçer |
|---|---|---|
| Düz metin | `.txt` `.text` `.log` `.json` `.csv` `.tsv` `.xml` `.ini` `.cfg` `.conf` `.yaml` `.yml` `.nfo` | Metin olduğu gibi; UTF‑8, BOM'lu UTF‑16/32 ya da eski Not Defteri dosyalarının Windows‑1254 kod sayfası |
| Markdown | `.md` `.markdown` | Metin; `- [ ]` / `- [x]` görev listeleri yapılacaklar listesi olur |
| HTML | `.html` `.htm` | Etiketler, betikler ve stiller atılmış okunur metin |
| Zengin metin / Word | `.rtf` `.docx` | Biçimsiz metin; resim ve tablolar alınmaz |

8 MB'tan büyük dosyalar ve metin olmayan dosyalar reddedilir. Ctrl+Shift+S açık notu şifresiz kopya olarak kaydeder: **TXT** (metin yazıldığı gibi) ya da **Markdown** (başlık `#` başlığı, listeler görev listesi olarak). Kaydetme yalnızca metin uzantılarına izin verir; bir not asla kasanın ya da yedeğin üstüne yazılamaz.

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

- `src/Notlar/` — WPF uygulaması. `Web/link.js` telefonun bağlantı durum makinesidir (saf; node altında sahte saatle test edilir). Ana pencere konuya göre bölünmüştür (`MainWindow.List.cs`, `.Editor.cs`, `.Attachments.cs`, `.Files.cs`, `.Sync.cs`, `.Tray.cs`); `Sync/` sunucu, oturumlar, protokol, sertifika ve QR kodunu; `Web/` telefon uygulamasını küçük ES modülleri halinde (`state`, `ui`, `store`, `sync`, `list`, `editor`, `attachments`; düz JavaScript, WebCrypto, IndexedDB) içerir. Dosyalar bilerek 300 satır civarında tutulur.
- `build.cmd` — kendi kendine yeten x64 derlemeyi `app/` içine yayınlar ve kök başlatıcıyı derler.
- `test.cmd` — geçici verilerle kontrolleri çalıştırır (`NOTLAR_SHOT=docs` ile bu README'deki ekran görüntüleri örnek notlardan yeniden üretilir): şifreleme ve tahrif tespiti, ekler, yedekler, silme politikası, toplu işlemler, tepsi davranışı, arayüz yerleşimi ve telefonu canlandıran bir Node betiği (eşleştirme kodu, iki yönlü eşitleme, çakışmalar, bayt bayt aynı dosyalar).
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
