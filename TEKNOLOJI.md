# Teknoloji Kararı

Tarih: 20 Kasım 2026
Hedef: Önce Windows 11, sonra Linux. Modern masaüstü arayüzü.

## Karar: C# / .NET 10 + Avalonia UI

| Katman | Seçim |
|---|---|
| Dil / çalışma zamanı | C# 14, .NET 10 (LTS) |
| Arayüz | Avalonia UI (Fluent tema) + CommunityToolkit.Mvvm |
| Grafikler (frame-time, sıcaklık) | LiveCharts2 veya ScottPlot.Avalonia |
| Ayrıcalıklı yardımcı | .NET Worker Service — Windows'ta Windows Service, Linux'ta systemd servisi + polkit |
| Arayüz ↔ yardımcı iletişimi | Named pipe (Windows, ACL ile kısıtlı) / Unix domain socket (Linux), JSON mesajlar |
| Ayar veritabanı | Uygulamayla gelen TOML/JSON dosyaları (`Tomlyn` veya `System.Text.Json`) |
| Ölçüm | PresentMon CLI (Windows), MangoHud log (Linux) — alt süreç olarak çalıştırılıp CSV ayrıştırılır |
| Testler | xUnit; platforma özel testler için sanal makinelerde uygula/geri al döngüsü |
| Paketleme | Windows: Inno Setup veya WiX (servis kurulumu yönetici gerektirdiği için MSI/EXE yükleyici). Linux: .deb / .rpm / AppImage. **Flatpak değil** — sandbox sistem ayarı yapmayı engeller. |
| Kod imzalama | Azure Trusted Signing veya OV sertifika |

## Neden bu, neden Rust + Tauri değil

Araştırma raporunda iki aday vardı. Kararı belirleyen noktalar:

1. **İşin %80'i Windows sistem API'leri.** Registry, WMI (`System.Management`), servisler, güç planları, ETW, Device Guard sorgulama — .NET'te bunların hepsi birinci sınıf, belgelenmiş kütüphanelerle gelir. Rust'ta `windows-rs` ile yapılabilir ama WMI ve ETW belirgin şekilde daha zahmetli.
2. **Tek dil.** Tauri, Rust (çekirdek) + TypeScript (arayüz) demek; Avalonia ile arayüz, çekirdek ve yardımcı servis aynı dilde, aynı modelleri paylaşır.
3. **Linux'ta tutarlı arayüz.** Avalonia kendi çizimini yapar, her dağıtımda aynı görünür. Tauri Linux'ta WebKitGTK kullanır; sürüm ve görüntü farklılıkları yaşanabiliyor.
4. **Kurulum sürtünmesi yok.** Bu bilgisayarda .NET 10 SDK zaten kurulu; Rust + MSVC Build Tools ise birkaç GB'lık ek kurulum gerektirirdi.

Kabul edilen dezavantajlar:
- Uygulama boyutu daha büyük (kendi içinde .NET taşıyan tek dosya ~40–70 MB; Tauri ~10 MB). Hedef kitle için sorun değil.
- Bellek kullanımı biraz daha yüksek. Oyun sırasında çalışacak olan **yardımcı servis** Native AOT ile derlenerek küçük ve hafif tutulacak; arayüz oyun sırasında kapalı olabilir.

## Proje yapısı (taslak)

```
src/
  Optimizer.Core/              # Platformdan bağımsız: ayar modeli, journal, kural motoru, risk seviyeleri
  Optimizer.Platform.Windows/  # Envanter (WMI, DXGI, ekran), registry/powercfg uygulayıcıları
  Optimizer.Platform.Linux/    # (Faz 6) /sys, sysctl drop-in, power-profiles, GameMode
  Optimizer.Helper/            # Yönetici yetkili servis (Faz 2'de eklenecek)
  Optimizer.App/               # Avalonia arayüzü (MVVM)
tweaks/
  windows/*.toml               # Bildirimsel ayar tanımları
  linux/*.toml
tests/
  Optimizer.Core.Tests/
  Optimizer.Platform.Windows.Tests/
```

Faz 1 (teşhis aracı) salt okuma olduğu için **yönetici yetkisi ve yardımcı servis gerekmez**; yardımcı Faz 2'de eklenir. Böylece ilk sürüm en düşük riskle çıkar.
