# Performance Optimizer

Oyun performansını etkileyen sistem ayarlarını teşhis eden masaüstü uygulaması (Windows; Linux desteği planlı).

**Mevcut durum — Faz 1 (teşhis):** Uygulama yalnızca okur, hiçbir ayarı değiştirmez ve yönetici yetkisi istemez.

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

Komut satırı raporu (JSON çıktısı için `--json`):

```powershell
dotnet run --project src/Optimizer.Cli
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
| `tests/Optimizer.Core.Tests` | Kural testleri |

Ayrıntılar: [ARASTIRMA.md](ARASTIRMA.md), [TEKNOLOJI.md](TEKNOLOJI.md)
