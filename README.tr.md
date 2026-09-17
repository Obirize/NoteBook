# Notlar

Windows için sade, yerel ve arka planda şifrelenen not defteri. C# / WPF ve .NET 10. Tarayıcı, hesap oluşturma veya internet gerektirmez.

## Kullanım

**`Not Defteri.exe`** dosyasını açın ve yazın. Uygulama parola istemez; Windows hesabınız üzerinden şifreli notları otomatik açar. `app` klasörü uygulama dosyalarını ve çalışma zamanını içerir; başlatıcıyla birlikte tutulmalıdır.

- Solda arama ve notlar, sağda başlık ve yazı alanı.
- Yazma durduktan 650 ms sonra otomatik kayıt. Not değiştirme ve kapatma öncesi de kaydedilir.
- Başlıkta Enter, yazı alanına geçer.
- Sabitlenenler listenin başında yer alır.
- Silinenler “Son silinenler” bölümüne taşınır ve 30 gün boyunca geri yüklenebilir; her kartta kalan süre görünür. 30 günü dolan notlar uygulama açılışında kalıcı olarak silinir. Son silinenlerdeki bir not “Kalıcı sil” ile hemen de silinebilir; bu işlem onay ister ve geri alınamaz. Kalıcı silme ana kaydı, önceki şifreli kaydı ve eski dosya arşivindeki kopyayı birlikte temizler.
- Yazı alanlarında sağ tık: Kes / Kopyala / Yapıştır / Tümünü seç. Not kartında sağ tık: Sabitle, TXT dışa aktar, Son silinenlere taşı; son silinenlerde Geri yükle ve Kalıcı sil.
- “Seç” düğmesi toplu seçim modunu açar: kartlarda onay kutuları belirir, “Tümünü seç” ile hepsi işaretlenir. Seçilenler tek adımda son silinenlere taşınır, geri yüklenir veya (son silinenlerde) kalıcı olarak silinir. Toplu taşıma da “Geri al” ile geri alınabilir.
- Alt solda TXT açma ve şifreli yedek düğmeleri vardır. Uygulama yalnızca koyu temadadır.
- Pencere başlığı ve ince kaydırma çubukları koyu temayla uyumludur. Fare tekerleği not listesinde ve yazı alanında aynı şekilde çalışır: kart sayısıyla değil piksel bazında (Windows fare ayarındaki satır sayısı × 28 piksel; varsayılan 3 satırda 84 piksel), kısa bir yumuşatmayla ve tekerlek bırakıldığında uzun süre kaymaya devam etmeden. Yazarken otomatik kayıt listenin kaydırma konumunu sıfırlamaz. Başlıktan sürükleme, çift tıklayarak büyütme, pencere kenarından boyutlandırma desteklenir.

| Kısayol | İşlem |
| --- | --- |
| Ctrl+N | Yeni not |
| Ctrl+O | TXT dosyasını yeni not olarak aç |
| Ctrl+Shift+S | Açık notu şifresiz TXT kopyası olarak kaydet |
| Ctrl+K / Ctrl+F | Arama |
| Ctrl+S | Hemen kaydet / hatalı kaydı yeniden dene |
| Ctrl+Z / Ctrl+Y | Metin düzenlemesini geri al / yinele |
| Delete (liste odaktayken) | Notu son silinenlere taşı; son silinenlerde kalıcı sil (onaylı) |
| Ctrl+A (seçim modunda) | Tümünü seç |
| Esc (seçim modunda) | Seçim modundan çık |

## TXT dosyalarını Notlar ile açma

`Not Defteri.exe dosya.txt` komutu (veya Gezgin'de "Birlikte aç → Notlar") dosyayı yeni bir not olarak kasaya aktarır; uygulama zaten açıksa aynı pencereye iletilir, ikinci pencere açılmaz. **Notlar bir dosya düzenleyici değildir:** TXT içe aktarılır, düzenlemeler kasada tutulur, orijinal dosya değişmez. Dosyanın kendisini düzenlemek için TXT olarak dışa aktarma kullanılır.

`Not Defteri.exe --new` doğrudan yeni bir not açar (masaüstü sağ tık menüsündeki “Yeni not (Notlar)” bunu kullanır).

Taşınabilir kullanımda `txt-kaydet.cmd`, Notlar'ı `.txt` için "Birlikte aç" listesine ekler (yalnızca geçerli Windows hesabı, yönetici gerekmez). Varsayılan yapmak için *Ayarlar → Uygulamalar → Varsayılan uygulamalar → Dosya türüne göre → .txt → Notlar* seçilir; Windows bu son adımı yalnızca kullanıcıya bırakır. `txt-kaydi-kaldir.cmd` kaydı geri alır. Windows Not Defteri'ni kaldırmak önerilmez; bazı programlar ve betikler `notepad.exe`'yi doğrudan çağırır.

## Kurulum paketi

`build-setup.cmd`, [Inno Setup 6](https://jrsoftware.org/isdl.php) ile `dist/NoteBook-Setup-<sürüm>.exe` üretir (`setup/Notlar.iss`). Kurulum kullanıcı düzeyindedir: yönetici izni istemez, `%LocalAppData%\Programs\Notlar` altına kurulur, notlar `…\Notlar\data` içinde tutulur ve kaldırmada silinmez. Kurulumda seçilebilen bütünleşmeler:

- **.txt için “Birlikte aç” ve Varsayılan uygulamalar kaydı.** Windows Not Defteri kaldırılmaz veya değiştirilmez; Notlar yanına eklenir. Windows, dosya türünün varsayılanını yalnızca kullanıcının seçmesine izin verir (kurulum programları bunu sessizce değiştiremez); kurulum sonunda Ayarlar sayfası açılabilir: *Varsayılan uygulamalar → Dosya türüne göre → .txt → Notlar*.
- **Sağ tık → “Yeni not (Notlar)”** masaüstünde ve klasör arka planında. Dosya oluşturmaz; uygulamada yeni not açar. Windows'un “Yeni → Metin Belgesi” girdisi olduğu gibi kalır.

Kurulum sihirbazı sırası: (1) hedef klasör, (2) isteğe bağlı bütünleşmeler, (3) kurulum, (4) son sayfada "Notlar'ı başlat" ve ".txt için varsayılan uygulamayı seçmek üzere Ayarlar'ı aç" seçenekleri. Ayarlar sayfasında *.txt* satırında Notlar seçilir; bu bir kez yapılır.

Taşınabilir klasörden kuruluma geçerken `data` klasörü kurulu konuma kopyalanabilir; aynı Windows hesabında açılır.

## Otomatik güncelleme

Uygulama her açılışta GitHub Releases'ı (`Updater.Repository`, `src/Notlar/Updater.cs`) kontrol eder. Yeni sürüm varsa alt çubukta **"Sürüm x.y.z hazır · Güncelle"** görünür; tıklanınca kurulum dosyası indirilir, yayınla birlikte gelen SHA-256 ile doğrulanır, sessizce kurulur ve uygulama yeniden açılır. Kullanıcı sil-kur yapmaz; notlar yerinde kalır.

Bu, uygulamanın yaptığı **tek ağ isteğidir**; yalnızca sürüm bilgisi alınır, kimlik/telemetri gönderilmez. Kapatmak için `data` klasörüne `guncelleme-kapali` adında boş bir dosya konur. `Updater.Repository` doldurulana kadar kontrol yapılmaz.

### Yayınlama (GitHub)

1. `src/Notlar/Updater.cs` içinde `Repository = "kullanıcı/depo"` yazın.
2. Depoyu GitHub'a gönderin; `.github/workflows/release.yml` hazırdır.
3. Sürüm etiketi atın: `git tag v1.0.1 && git push --tags`.
4. GitHub Actions kurulum dosyasını derler (`NOTLAR_VERSION` etiketten alınır), SHA-256 dosyasını üretir ve Release'e ekler. Kullanıcılar bir sonraki açılışta güncellemeyi görür.

Kurulum dosyası kod imzalı değildir; tarayıcıdan ilk indirmede SmartScreen uyarısı çıkabilir ("Daha fazla bilgi → Yine de çalıştır"). Uygulama içi güncellemede bu uyarı çıkmaz. İmza sertifikası (OV/EV) alındığında `release.yml` içinde `signtool` adımı eklenir.

## Diller

Arayüz 13 dilde: Türkçe, İngilizce, İspanyolca, Çince, Hintçe, Arapça (sağdan sola), Portekizce, Rusça, Japonca, Almanca, Fransızca, Endonezce, Korece. Uygulama Windows görüntü dilini izler; sol alttaki küre düğmesiyle değiştirilir ve `data/settings.json` içine kaydedilir. Metinler `src/Notlar/Languages/<kod>.json` dosyalarındadır; yeni dil için `en.json` kopyalanıp çevrilir ve `L10n.Languages` listesine bir satır eklenir.

## Şifreleme nasıl çalışıyor?

Not içeriği, başlıklar, tarihler ve eski dosya arşivleri AES-256-GCM ile şifrelenir. Rastgele 256 bit içerik anahtarı, Windows DPAPI `CurrentUser` ile korunarak kasaya yazılır. Anahtar dosyada açık olarak bulunmaz; her kayıtta yeni nonce kullanılır. İçerik veya anahtar değiştirildiğinde doğrulama başarısız olur.

Uygulama için ayrı parola, kurulum ekranı veya beş dakikada bir kilit yoktur. Normal Windows oturum koruması geçerlidir. Bu, **not dosyasını tek başına kopyalayan kişinin doğrudan okumasını önlemeye** yöneliktir. Aynı Windows hesabı altında çalışan bir program veya açık oturumunuza erişen kişi notları açabilir. Zararlı yazılım, yönetici yetkisiyle hesabın ele geçirilmesi, ekran/klavye kaydı veya bellek dökümüne karşı mutlak koruma değildir.

DPAPI dayanağı: [Microsoft DataProtectionScope](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope). İçerik şifreleme: [Microsoft AesGcm](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm).

Uygulamada güncelleme kontrolü dışında ağ isteği yoktur; telemetri yoktur. Bağımsız güvenlik denetiminden henüz geçmemiştir.

## TXT ve kasa biçimi

İç kayıt `.vault` olarak kalır; bu uygulamanın şifreli veri biçimidir. Düz metin alışverişi için UTF-8 `.txt` kullanılır. TXT açma dosyayı yeni not olarak kasaya ekler; orijinal dosyayı değiştirmez. Başlık dosya adından, not içeriği dosyanın tamamından alınır. Düzenlemeler kasaya otomatik kaydedilir.

Üst araç çubuğundaki dışa aktarma düğmesi veya Ctrl+Shift+S, not içeriğini UTF-8 TXT olarak kaydeder; başlık önerilen dosya adı olur. **Dışa aktarılan TXT şifresizdir.** Bu bilgi kaydetme penceresinde gösterilir. Bu kopya başka cihaz ve metin editörlerinde açılabilir; uygulamadaki şifreli not korunur.

İçe aktarma UTF-8, BOM işaretli UTF-16/UTF-32 ve eski Türkçe Windows-1254 metinlerini destekler. Dosya boyutu en fazla 8 MB olmalıdır. Boş satırlar, Unicode karakterler ve satır sonları korunur. Dosya ilişkilendirmesi değiştirilmez; dosyayı uygulamanın TXT açma düğmesinden seçin.

## Mevcut notlar

İlk açılışta `data/notes.json` ve `data/notes.backup.json` otomatik aktarılır. Kimlikler, tarihler, metinler, sabitleme ve silinme bilgileri korunur. Eski dosyaların tamamı (RTF ve kullanılmayan alanlar dahil) şifreli arşiv olarak kasanın içinde tutulur. Şifreli kayıt ve Windows tarafından korunan anahtar doğrulanmadan eski düz metin dosyaları kaldırılmaz. Aktarım sırasında değişmiş dosya silinmez. Eski uygulamayı aynı anda kullanmayın.

Önceki sürümde zaten parola oluşturulmuşsa **yalnızca dönüşüm için bir kez** mevcut parola/anahtar gerekir. Doğru açılan kasa Windows korumasına dönüştürülür; sonraki açılışlar parolasızdır. Hiç parola oluşturulmamışsa bu ekran gösterilmez.

Dosya silme işlemi eski disk kalıntılarını, SSD kurtarma olasılığını veya başka yerdeki önceki yedekleri ortadan kaldırma garantisi vermez.

## Dosyalar ve yedek

- `data/notes.vault`: ana şifreli kayıt.
- `data/notes.vault.bak`: önceki şifreli kayıt.

Yedek düğmesi şifreli dosyanın kopyasını alır. **Bu yedek aynı Windows hesabının DPAPI anahtarlarına bağlıdır; tek başına başka PC/hesap için taşınabilir yedek değildir.** Windows'u yeniden kurmak veya kullanıcı profilini kaybetmek bu kopyaların açılamamasına neden olabilir. Taşınabilir şifreli kurtarma henüz yoktur; tekil notlar TXT olarak dışa aktarılabilir. Yedeğin hesap bağımlılığı kaydetme penceresinin başlığında belirtilir.

Ana kayıt açılamazsa uygulama önceki şifreli kaydı denemeyi sorar. Doğrulanmış yedek atomik olarak geri yüklenir; hasarlı kayıt `.damaged-...` olarak korunur. Bozuk dosyalar sessizce boş notlara dönüştürülmez. Disk doluluğu/izin hatasında son değişiklik açık tutulur ve hata gösterilir.

## Telefon desteği ve senkron planı

Telefon eşzamanlaması henüz yoktur. Veri modeli buna hazırdır: not kimlikleri, UTC tarihler, `Revision` sayaçları ve silinme işaretleri (`Deleted`/`DeletedAt`) korunur; içerik anahtarı Windows koruma katmanından ayrıdır.

Notlar şifreli olduğu için telefonda çözebilecek bir yazılım gerekir; bulut sürücüsüne kopyalamak tek başına yetmez. İki yol vardır:

1. **PWA (tarayıcıda çalışan web uygulaması)** — mağaza ve geliştirici hesabı gerekmez; Safari/Chrome'da “Ana ekrana ekle” ile uygulama gibi açılır. AES-256-GCM ve PBKDF2 tarayıcının WebCrypto API'sinde vardır, kasa biçimi aynen okunabilir. Önerilen ilk adım budur.
2. **Yerel uygulama (iOS/Android)** — daha iyi çevrimdışı ve bildirim desteği; iOS için Apple geliştirici hesabı ve mağaza süreci gerekir. PWA doğrulandıktan sonra ikinci aşama.

Aşamalar:

- **Aşama 1 — Taşınabilir anahtar (yalnızca Windows tarafı).** Kasadaki içerik anahtarı, kullanıcının seçtiği parolayla (PBKDF2 600k) ikinci kez sarmalanır (mevcut sürüm 1 zarf mantığı). Böylece kasa DPAPI olmadan da açılabilir; aynı zamanda gerçek taşınabilir yedek olur. Windows'ta parola gerekmez, yalnızca telefon/yedek için sorulur.
- **Aşama 2 — Salt-okunur telefon.** Kasa dosyası kullanıcının kendi bulut klasörüne (OneDrive/iCloud Drive/Google Drive) yazılır; PWA dosyayı seçip parolayla çözer ve notları gösterir. Sunucu yoktur, sunucu notları okuyamaz.
- **Aşama 3 — Çift yönlü senkron.** Her cihaz kendi değişikliklerini not bazında (kimlik + revizyon + tarih) yazar; birleştirme “son revizyon kazanır”, çakışan iki düzenleme ise iki ayrı not olarak korunur (veri kaybı yok). Silmeler `DeletedAt` ile 30 gün taşınır. Aktarım yine kullanıcının bulut klasörü üzerindendir; isteğe bağlı küçük bir aracı sunucu yalnızca şifreli blob taşır.
- **Aşama 4 — Yerel uygulama** (gerekirse).

## Geliştirme

.NET 10 SDK ve Windows gerekir. `build.cmd`, `app/` içine self-contained x64 dağıtım üretir ve kökteki başlatıcıyı derler. Varsa `.tools/dotnet` içindeki SDK'yı kullanır. Kaynak: `src/Notlar/`.

`test.cmd`: geçici verilerle şifreleme, otomatik açma, eski parola kasasını dönüştürme, anahtar/içerik tahrifi, aktarım, kayıt hatası, otomatik kayıt, arama, silme/geri alma, 30 günlük otomatik temizleme, toplu seçim/silme/geri yükleme, onaylı kalıcı silme ve listede/yazı alanında yumuşak kaydırmayı test eder. Gerçek `data` klasöründeki notlara yazmaz. Görseller `artifacts/` içine gider. Eski parola testleri yalnızca geçmiş sürüm uyumluluğunu doğrular.

`notes.ico` çok boyutlu (16–256 px) uygulama simgesidir. `.tools`, `app`, `data` ve derleme çıktıları kaynak paylaşımına dahil edilmemelidir.
