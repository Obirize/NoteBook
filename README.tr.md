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
| Ctrl+Shift+A | Açık nota fotoğraf veya video ekle |
| Ctrl+V (panoda resim veya medya dosyası varken) | Açık nota ek olarak yapıştır |
| Ctrl+K / Ctrl+F | Arama |
| Ctrl+S | Hemen kaydet / hatalı kaydı yeniden dene |
| Ctrl+Z / Ctrl+Y | Metin düzenlemesini geri al / yinele |
| Delete (liste odaktayken) | Notu son silinenlere taşı; son silinenlerde kalıcı sil (onaylı) |
| Ctrl+A (seçim modunda) | Tümünü seç |
| Esc (seçim modunda) | Seçim modundan çık |

## TXT dosyalarını Notlar ile açma

`Not Defteri.exe dosya.txt` komutu (veya Gezgin'de "Birlikte aç → Notlar") dosyayı yeni bir not olarak kasaya aktarır; uygulama zaten açıksa aynı pencereye iletilir, ikinci pencere açılmaz. **Notlar bir dosya düzenleyici değildir:** TXT içe aktarılır, düzenlemeler kasada tutulur, orijinal dosya değişmez. Dosyanın kendisini düzenlemek için TXT olarak dışa aktarma kullanılır.

`Not Defteri.exe --new` doğrudan yeni bir not açar (masaüstü sağ tık menüsündeki “Yeni not (Notlar)” bunu kullanır).

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

Fotoğraf ve videolar kasa dosyasına konmaz. Her ek `data/attachments/<id>.bin` olarak, kendi rastgele 256 bit anahtarıyla 1 MB'lık parçalar halinde AES-256-GCM ile şifrelenir; anahtar, dosya adı, boyut ve özet yalnızca şifreli not defterinin içinde durur. Her parça dosya başlığını, ek kimliğini, parça sırasını ve "son parça" işaretini doğrular; parçalar yeniden sıralanamaz, atılamaz, kesilemez veya başka dosyaya taşınamaz. Dosyanın yeniden şifrelenmesi gerekmediği için yedek (ileride başka bir cihaz) dosyayı olduğu gibi, bayt bayt alır. Video oynatılırken oynatıcıya geçici klasörde çözülmüş bir kopya verilir; görüntüleyici kapanınca silinir.

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
- `data/attachments/`: şifreli fotoğraf ve videolar, her ek için bir dosya.

**Yedek** (kenar çubuğundaki kutu simgesi) → *Yedek al…*, seçtiğiniz parolayla korunan tek bir `.vault` dosyası yazar (PBKDF2 600k + AES-256-GCM, kendi rastgele anahtarıyla). Bu dosya o parolayla **her bilgisayarda** açılır; Windows hesabına bağlı değildir. *Yedekten geri yükle…* yedeği notlarınızla birleştirir: yerelde olmayan notlar eklenir, her notun daha yeni revizyonu kazanır, hiçbir şey silinmez. Aynı Windows hesabından alınmış düz `notes.vault` kopyası da geri yüklenebilir. Not defterinde ek varsa yedeğin yanına `<ad>.vault.files` klasörü yazılır; ikisini birlikte saklayın. Geri yükleme eksik dosyaları kopyalar.

TXT dosyaları pencereye sürükleyip bırakarak da eklenebilir.

## Fotoğraf ve video

Bir not açıkken üst çubuktaki ataş düğmesi (Ctrl+Shift+A), pencereye sürükleyip bırakma veya panoda resim varken Ctrl+V nota fotoğraf ya da video ekler. Küçük resimler metnin üstünde görünür; tıklayınca tam boy görüntüleyici açılır, videolar uygulama içinde oynatılır (boşluk tuşu duraklatır, Esc kapatır). Sağ tık menüsünden dosyanın şifresiz bir kopyası kaydedilebilir veya ek nottan kaldırılabilir. Diskteki orijinal dosya hiçbir zaman değiştirilmez veya silinmez.

Ana kayıt açılamazsa uygulama önceki şifreli kaydı denemeyi sorar. Doğrulanmış yedek atomik olarak geri yüklenir; hasarlı kayıt `.damaged-...` olarak korunur. Bozuk dosyalar sessizce boş notlara dönüştürülmez. Disk doluluğu/izin hatasında son değişiklik açık tutulur ve hata gösterilir.

## Telefonla eşitleme (iPhone)

Notlar, fotoğraflar ve videolar aynı Wi‑Fi'daki bilgisayar ile iPhone arasında doğrudan eşitlenir. Hesap, bulut ve aracı sunucu yoktur: sunucu Windows uygulamasının kendisidir, telefon tarafı ise o bilgisayarın sunduğu ve ana ekrana eklenen bir web uygulamasıdır. Hiçbir yerde yayın yapılmaz, hiçbir ücret ödenmez.

Kurulum bir kez yapılır (kenar çubuğunun altındaki telefon düğmesi üç adımı QR kodlarıyla gösterir):

1. **Güven belgesi.** Bilgisayar kendi sertifika otoritesidir. İlk kodu iPhone kamerasıyla okutun; sayfa bir yapılandırma profili indirir. *Ayarlar → Genel → VPN ve Aygıt Yönetimi*'nden kurun, sonra *Ayarlar → Genel → Hakkında → Sertifika Güven Ayarları*'ndan açın. İki tarafta gösterilen SHA‑256 parmak izini karşılaştırın.
2. **Ana ekran uygulaması.** İkinci kodu okutun (ya da Safari'de `https://<bilgisayar>.local:47831/` adresini açın) ve *Paylaş → Ana Ekrana Ekle* deyin. Bundan sonra Notlar'ı ana ekrandan açın: iOS, ana ekrandaki web uygulamalarına Safari'den ayrı bir depolama verir.
3. **Eşleştirme kodu.** Ana ekrandaki uygulamada *Eşleştir*'e dokunun ve bilgisayarda görünen 6 haneli kodu yazın. Her 60 saniyede yeni kod gelir (yazmayı bitirmeniz için bir önceki birkaç saniye daha kabul edilir), beş yanlış deneme kodu erken değiştirir ve kodlar yalnızca o pencere açıkken vardır. Kurulum sayfasındaki *Bağlantıyı sına* düğmesi belgenin tam güvenilir olup olmadığını söyler.

Fotoğraf ve videolar hiçbir zaman yeniden sıkıştırılmaz: telefon verilen dosyayı olduğu gibi şifreler, bilgisayar da tam o baytları saklar (uygulama iOS'tan orijinalleri ister; HEIC ve HEVC/MOV dokunulmadan geçer; iOS boyut sorarsa *Gerçek Boyut*'u seçin). Windows'ta HEIC/HEVC önizlemesi için Microsoft'un HEIF/HEVC uzantıları gerekir; yoksa dosya yine saklanır ve kopyası alınabilir.

Gizlilik nasıl korunuyor: telefon ile bilgisayar 256 bitlik bir eşitleme anahtarını paylaşır; bu anahtar yalnızca bir kez, TLS üzerinden, eşleştirme kodu karşılığında gider. İki taraf bundan bir kimlik doğrulama anahtarı (her bağlantıda karşılıklı HMAC sınaması) ve bir içerik anahtarı (AES‑256‑GCM) türetir. Her not bu içerik anahtarıyla şifreli olarak taşınır ve telefonda da öyle saklanır; ekler yukarıda anlatılan şifreli dosyalar olarak, bayt bayt aktarılır. Sunucu yalnızca yerel ağdaki adreslere yanıt verir ve yalnızca anahtarı kanıtlayan cihazla konuşur. Evden uzaktaki telefon çevrimdışı çalışmaya devam eder, Wi‑Fi'a dönünce birleşir.

Birleştirme: bir notun yüksek revizyonu kazanır; iki cihaz aynı revizyonu düzenlemişse yeni olan not olarak kalır, diğeri "çakışma kopyası" olarak saklanır — hiçbir şey sessizce kaybolmaz. Kalıcı silmeler 180 gün hatırlanır; telefon silinmiş bir notu geri getiremez.

Windows Defender Güvenlik Duvarı bir kez özel ağlarda izin ister. Bilgisayar TCP 47831 (HTTPS + WebSocket) ve 47832 (belgeyi veren düz kurulum sayfası) bağlantı noktalarını dinler.

## Geliştirme

.NET 10 SDK ve Windows gerekir. `build.cmd`, `app/` içine self-contained x64 dağıtım üretir ve kökteki başlatıcıyı derler. Varsa `.tools/dotnet` içindeki SDK'yı kullanır. Kaynak: `src/Notlar/`.

`test.cmd`: geçici verilerle şifreleme, otomatik açma, eski parola kasasını dönüştürme, anahtar/içerik tahrifi, aktarım, kayıt hatası, otomatik kayıt, arama, silme/geri alma, 30 günlük otomatik temizleme, toplu seçim/silme/geri yükleme, onaylı kalıcı silme ve listede/yazı alanında yumuşak kaydırmayı test eder. Gerçek `data` klasöründeki notlara yazmaz. Görseller `artifacts/` içine gider. Eski parola testleri yalnızca geçmiş sürüm uyumluluğunu doğrular.

`notes.ico` çok boyutlu (16–256 px) uygulama simgesidir. `.tools`, `app`, `data` ve derleme çıktıları kaynak paylaşımına dahil edilmemelidir.
