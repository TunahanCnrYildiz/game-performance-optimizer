# Performance Optimizer

Oyun performansını etkileyen sistem ayarlarını teşhis eden masaüstü uygulaması (Windows; Linux desteği planlı).

**Mevcut durum — Faz 2 (güvenli düzeltme):** Uygulama sistemi tarar, sorunları listeler ve bir kısmını kullanıcının onayıyla düzeltebilir. Yönetici yetkisi istemez.

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
| `tests/Optimizer.Platform.Windows.Tests` | Kayıt defteri yürütücüsü testleri (yalnızca geçici bir test anahtarında çalışır) |

Ayrıntılar: [ARASTIRMA.md](ARASTIRMA.md), [TEKNOLOJI.md](TEKNOLOJI.md)
