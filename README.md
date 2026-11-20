# Performance Optimizer

Oyun performansını etkileyen sistem ayarlarını teşhis eden masaüstü uygulaması (Windows; Linux desteği planlı).

**Mevcut durum — Faz 4 (FPS ölçümü):** Uygulama sistemi tarar, sorunları listeler, bir kısmını kullanıcının onayıyla düzeltir, oyun oynarken arka planda neyin kaynak tükettiğini ölçer ve bir ayarın FPS'e gerçek etkisini önce/sonra ölçümüyle gösterir. Yönetici yetkisi istemez.

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
dotnet run --project src/Optimizer.App
```

Komut satırı aracı:

```powershell
dotnet run --project src/Optimizer.Cli                 # rapor (JSON için --json)
dotnet run --project src/Optimizer.Cli -- history      # değişiklik geçmişi
dotnet run --project src/Optimizer.Cli -- revert <id>  # tek değişikliği geri al
dotnet run --project src/Optimizer.Cli -- revert-all   # her şeyi geri al
dotnet run --project src/Optimizer.Cli -- sample 10    # şu an neyin kaynak kullandığını 10 sn ölç
dotnet run --project src/Optimizer.Cli -- bench <pid|ad> 30 "etiket"   # FPS ölçümü (--admin: yönetici izniyle)
```

Testler:

```powershell
dotnet test --solution PerformanceOptimizer.slnx
```

## Proje yapısı

| Proje | Görev |
|---|---|
| `Optimizer.Core` | Platformdan bağımsız envanter modeli ve teşhis kuralları |
| `Optimizer.Platform.Windows` | WMI, registry ve Win32 API ile salt okuma sistem taraması |
| `Optimizer.App` | Avalonia arayüzü |
| `Optimizer.Cli` | Komut satırı raporu |
| `tests/Optimizer.Core.Tests` | Kural ve değişiklik motoru testleri |
| `tests/Optimizer.Platform.Windows.Tests` | Kayıt defteri, süreç örnekleyici ve uçtan uca oturum testleri (yalnızca geçici test anahtarı ve testin kendi başlattığı süreçler üzerinde çalışır) |

Ayrıntılar: [ARASTIRMA.md](ARASTIRMA.md), [TEKNOLOJI.md](TEKNOLOJI.md)
