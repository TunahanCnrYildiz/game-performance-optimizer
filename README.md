# Performance Optimizer

Oyun performansını etkileyen sistem ayarlarını teşhis eden masaüstü uygulaması (Windows ve Linux).

**Mevcut durum — Faz 5 (gelişmiş ayarlar):** Uygulama sistemi tarar, sorunları listeler, bir kısmını kullanıcının onayıyla düzeltir, oyun oynarken arka planda neyin kaynak tükettiğini ölçer, bir ayarın FPS'e gerçek etkisini önce/sonra ölçümüyle gösterir ve "Gelişmiş" sekmesinde yönetici gerektiren birkaç ayarı güvenli şekilde değiştirebilir. Uygulamanın kendisi yönetici olarak çalışmaz.

## Linux

- Teşhis: dağıtım/çekirdek, işlemci, bellek, ekran kartları (`/sys/class/drm`), ekranlar (`xrandr`), güç profili ve işlemci frekans sürücüsü, `vm.max_map_count`, split-lock ayarı, Feral GameMode, sched_ext, diskler. Kurallar: düşük `vm.max_map_count`, güç tasarrufu profili, `acpi-cpufreq` ile `powersave`, GameMode kurulu değil (dağıtıma uygun kurulum komutuyla), split-lock bilgisi; ayrıca yenileme hızı, bellek, disk ve pil kuralları.
- Değişiklikler: `vm.max_map_count` ve `kernel.split_lock_mitigate` yalnızca uygulamanın kendi dosyasına (`/etc/sysctl.d/99-performance-optimizer.conf`) yazılır; `pkexec` ile, izin listesi denetimiyle. Güç profili yönetici izni olmadan `powerprofilesctl` ile değiştirilir. Hepsi geri alınabilir.
- Oyun oturumları: süreç başına işlemci, bellek, G/Ç ve (DRM fdinfo ile) ekran kartı kullanımı; Proton oyunları Windows `.exe` adıyla tanınır. Oyun sırasında uygulama kısma Linux'ta yok: yönetici olmayan bir program başka bir sürecin önceliğini düşürebilir ama geri yükseltemez.
- FPS: Linux'ta çalışan bir oyuna dışarıdan bağlanıp kare süresi okumak mümkün olmadığından MangoHud kayıtları (`.csv`) içe aktarılır.
- Doğrulama: Linux kodu, örnek `/proc`–`/sys` dosyalarıyla her platformda; ayrıca Docker'da (`mcr.microsoft.com/dotnet/sdk:10.0`) gerçek Linux çekirdeği üzerinde test edilir. `pkexec` akışı ve masaüstü arayüzü gerçek bir Linux masaüstünde henüz denenmedi.

## Gelişmiş ayarlar (yönetici izni gerektirenler)

- HAGS aç/kapat, Bellek Bütünlüğü (HVCI) kapat/aç, oyun kütüphanesi klasörleri için Defender tarama istisnası, başlangıç programlarını devre dışı bırakma/etkinleştirme.
- Kalıcı bir yönetici servisi yoktur. Gerektiğinde uygulama yalnızca o işlem için kendi kopyasını UAC ile başlatır (`--elevated`). Bu kopya, istek dosyasındaki her değişikliği sabit bir izin listesine göre denetler; listede olmayan hiçbir anahtar, değer ya da klasör değiştirilmez. Aynı denetim geri alırken de yapılır, böylece kurcalanmış bir günlük kaydı keyfi bir sistem ayarı yazmak için kullanılamaz.
- İsteğe bağlı olarak önce Sistem Geri Yükleme noktası oluşturulur; oluşturulamazsa kullanıcıya sorulur.
- Yeniden başlatma gerektiren değişiklikler "yeniden başlatma bekliyor" olarak kaydedilir. Program bir sonraki oturum açılışında kendiliğinden açılır (tek seferlik RunOnce) ve 60 saniyelik geri sayımla onay ister; onaylanmazsa geri alınır.
- Kayma tespiti: Değişiklik geçmişinde, uygulanmış ama sonradan Windows ya da başka bir program tarafından değiştirilmiş ayarlar işaretlenir.
- Komut satırındaki `revert` / `revert-all`, yönetici gerektiren kayıtlar varsa kendini UAC ile yeniden başlatır.

## FPS ölçümü

- Kare zamanları Intel'in açık kaynak aracı [PresentMon](https://github.com/GameTechDev/PresentMon) (MIT) ile ölçülür. Araç git'te tutulmaz; `tools/presentmon/PresentMon.exe` olarak indirilip derlemede uygulamanın yanına kopyalanır.
- Her ölçüm bir etiketle kaydedilir (ör. "Discord açık"). Ortalama FPS, 1% low ve 0.1% low (%99 ve %99,9 kare süresine karşılık gelen FPS), takılma sayısı (medyanın 2,5 katından uzun kareler), ekrana gelmeyen kareler ve tahmini sınırlayan (ekran kartı / işlemci / kare sınırı) hesaplanır.
- İki etiket karşılaştırılırken fark, aynı etiketin tekrar ölçümleri arasındaki oynaklıkla kıyaslanır. Oynaklıktan küçük farklar "anlamlı fark yok" olarak gösterilir; tek ölçüm varsa ±%5 oynaklık varsayılır.
- Aynı kullanıcının başlattığı oyunlar yönetici izni olmadan ölçülür. Oyun yönetici olarak çalışıyorsa program, yalnızca PresentMon için yönetici izni (UAC) isteyerek tekrar dener.

## Oyun oturumları

- Steam/Epic kütüphanelerinden başlatılan oyunlar (ya da kullanıcının eklediği .exe'ler) otomatik algılanır.
- Oyun sürerken iki saniyede bir süreç başına işlemci, ekran kartı (Görev Yöneticisi'nin kullandığı "GPU Engine" sayaçları), G/Ç ve bellek ölçülür; ayrıca işlemcinin saat hızı ve güç/ısı sınırına takılıp takılmadığı izlenir.
- Oyun bitince oyun dışındaki en büyük tüketiciler raporlanır. Program kendi listesinden hiçbir şeyi kapatmaz.
- Kullanıcının seçtiği uygulamalar oyun süresince "verimlilik modu"na alınır (Görev Yöneticisi'ndeki ile aynı: düşük öncelik + EcoQoS) ve oyun bitince eski hâline döner. Windows bileşenleri ve anti-cheat yazılımları listeye eklenemez.
- Bu değişiklikler de geri alma günlüğüne "bekliyor" olarak yazılır; program oyun sırasında çökerse bir sonraki açılışta geri alınır.
- İzleme açıkken pencereyi kapatmak programı sistem tepsisine küçültür; tamamen kapatmak için tepsi menüsünden "Çık".

## Değişiklikler nasıl güvenli tutuluyor

- Hiçbir ayar kullanıcı "uygula" demeden değiştirilmez; düğmenin üstünde tam olarak hangi değerin neye döneceği yazar.
- Uygulamadan **önce** eski değerler `%LocalAppData%\PerformanceOptimizer\journal` altına JSON olarak kaydedilir; kayıt defteri anahtarları ayrıca `.reg` dosyası olarak yedeklenir.
- Değişiklikler sırayla uygulanır ve sonra doğrulanır. Herhangi bir adım başarısız olursa o ana kadar yapılanlar geri alınır.
- Ekran yenileme hızı gibi görüntüyü bozabilecek değişiklikler 15 saniye içinde onaylanmazsa kendiliğinden geri alınır. Uygulama bu sırada kapanırsa bir sonraki açılışta geri alınır.
- Geri alırken, sonradan kullanıcı ya da Windows tarafından değiştirilmiş bir değere dokunulmaz.
- Arayüz açılmasa bile komut satırından her şey geri alınabilir: `optimizer revert-all`.

Şu an düzeltilebilenler: ekran yenileme hızı, Oyun Modu, arka planda oyun kaydı, pencereli oyun iyileştirmeleri, fare ivmesi, güç tasarrufu planı.

## Ne kontrol ediliyor

- Monitörlerin desteklediği en yüksek yenileme hızında çalışıp çalışmadığı
- Masaüstünde monitörün anakarta mı ekran kartına mı bağlı olduğu
- Dizüstünde hibrit GPU durumu
- Ekran kartı sürücüsünün yaşı
- RAM hızı (XMP/EXPO), kanal düzeni ve kapasitesi
- Güç planı, güç modu, pil ve pil tasarrufu
- Oyun Modu, arka plan oyun kaydı, HAGS, pencereli oyun iyileştirmeleri, fare ivmesi
- Bellek Bütünlüğü (HVCI) ve Secure Boot
- Sistem diskinde boş alan, oyun kütüphanelerinin (Steam, Epic) HDD üzerinde olup olmadığı
- Başlangıç programları

## Çalıştırma

Gereksinim: .NET 10 SDK

```powershell
dotnet run --project src/Optimizer.App -f net10.0-windows   # Linux'ta: -f net10.0
```

Komut satırı aracı:

```powershell
dotnet run --project src/Optimizer.Cli -f net10.0-windows -- report   # Linux'ta -f net10.0; aşağıdakiler için de aynı
dotnet run --project src/Optimizer.Cli                 # rapor (JSON için --json)
dotnet run --project src/Optimizer.Cli -- history      # değişiklik geçmişi
dotnet run --project src/Optimizer.Cli -- revert <id>  # tek değişikliği geri al
dotnet run --project src/Optimizer.Cli -- revert-all   # her şeyi geri al
dotnet run --project src/Optimizer.Cli -- sample 10    # şu an neyin kaynak kullandığını 10 sn ölç
dotnet run --project src/Optimizer.Cli -- bench <pid|ad> 30 "etiket"   # FPS ölçümü, Windows (--admin: yönetici izniyle)
dotnet run --project src/Optimizer.Cli -- import <mangohud.csv> "etiket" # FPS kaydı içe aktarma, Linux
```

Testler:

```powershell
dotnet test --solution PerformanceOptimizer.slnx
```

## Proje yapısı

| Proje | Görev |
|---|---|
| `Optimizer.Core` | Platformdan bağımsız envanter modeli ve teşhis kuralları |
| `Optimizer.Platform.Windows` | Windows: WMI, kayıt defteri, Win32, PresentMon, UAC yardımcısı |
| `Optimizer.Platform.Linux` | Linux: /proc, /sys, sysctl, power-profiles-daemon, MangoHud, pkexec yardımcısı |
| `Optimizer.App` | Avalonia arayüzü |
| `Optimizer.Cli` | Komut satırı raporu |
| `tests/Optimizer.Core.Tests` | Kural ve değişiklik motoru testleri |
| `tests/Optimizer.Platform.Linux.Tests` | Linux toplayıcı, ayrıştırıcı, sysctl ve izin listesi testleri (örnek dosyalarla; biri yalnızca gerçek Linux'ta) |
| `tests/Optimizer.Platform.Windows.Tests` | Kayıt defteri, süreç örnekleyici ve uçtan uca oturum testleri (yalnızca geçici test anahtarı ve testin kendi başlattığı süreçler üzerinde çalışır) |

Ayrıntılar: [ARASTIRMA.md](ARASTIRMA.md), [TEKNOLOJI.md](TEKNOLOJI.md)

## Lisans

[MIT](LICENSE). Ölçüm için kullanılan Intel PresentMon ayrıca MIT lisanslıdır ve depoya dahil değildir; derlemede uygulamanın yanına `PresentMon-LICENSE.txt` ile birlikte kopyalanır.
