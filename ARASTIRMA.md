# Oyun Performansı Optimize Edici — Araştırma Raporu

Tarih: 20 Kasım 2026
Kapsam: Windows 11 (24H2/25H2), Linux (masaüstü dağıtımlar, Steam/Proton), macOS (Sequoia/Tahoe)

---

## 1. Önce gerçekçi beklenti: yazılım neyi değiştirebilir, neyi değiştiremez?

İnternetteki "FPS artırma" araçlarının çoğu ölçülebilir fark yaratmaz; bir kısmı ise sistemi bozar. Gerçek kazanç sırası kabaca şöyledir:

| Etki | Ne | Yazılımla yapılabilir mi? |
|---|---|---|
| Çok büyük | Yanlış yenileme hızı (144 Hz monitör 60 Hz'de çalışıyor), oyunun entegre GPU'da çalışması, termal kısıtlama (throttling), dizüstünün pil/sessiz modda olması | **Evet** — tespit edip düzeltebilirsin. En değerli kısım burası. |
| Büyük | Güncel GPU sürücüsü, RAM'in XMP/EXPO'suz düşük hızda çalışması, Resizable BAR kapalı, DLSS/FSR/XeSS kullanılmaması | Kısmen — sürücüyü ve RAM hızını **tespit edip** kullanıcıyı yönlendirebilirsin; BIOS ayarlarını yazılımdan değiştirmek **yapılmamalı**. |
| Orta | Güç planı, arka plan uygulamaları/başlangıç programları, overlay ve kayıt özellikleri (Game DVR), HAGS, VBS/Memory Integrity (CPU-sınırlı oyunlarda) | Evet, geri alınabilir şekilde. |
| Küçük / ölçülemez | "Nagle kapatma", "TCP optimizer", RAM temizleyiciler, timer resolution, servisleri toplu kapatma, HPET/dynamic tick oynamaları | Çoğu placebo veya zararlı. Yapma ya da sadece "deneysel" katmanda, ölçümle sun. |

**Çıkarım:** İyi bir program "500 registry ayarı uygulayan" değil, **sorunu teşhis eden, az sayıda kanıtlanmış değişikliği güvenli şekilde uygulayan ve etkisini ölçerek gösteren** programdır. Piyasadaki araçlardan ayrışmanın yolu da bu.

---

## 2. Programın mimarisi

### 2.1 Temel akış

```
Tespit (envanter)  →  Analiz (kural motoru)  →  Öneri listesi (risk etiketli)
      →  Yedek/anlık görüntü  →  Uygula  →  Doğrula  →  (gerekirse) Geri al
                         ↘  Ölçüm (önce/sonra benchmark)  ↗
```

### 2.2 Bileşenler

1. **Envanter modülü** — CPU, GPU(lar), sürücü sürümleri, RAM hızı vs. desteklenen hız, monitörler ve aktif yenileme hızları, güç kaynağı (pil/şarj), işletim sistemi build numarası, güvenlik durumu (VBS, HVCI, Secure Boot, TPM), disk boş alanı, sıcaklıklar.
   - Windows: WMI/CIM (`Win32_VideoController`, `Win32_PhysicalMemory`, `Win32_DeviceGuard`), DXGI (adaptör listesi), `EnumDisplaySettings`, `GetSystemPowerStatus`.
   - Linux: `/sys`, `/proc`, `lspci`, `vulkaninfo`, `xrandr`/`wlr-randr`/KMS, `powerprofilesctl`.
   - macOS: `system_profiler`, IOKit, `pmset -g`.
2. **Ayar (tweak) veritabanı** — Her ayar kod içine gömülmemeli, **bildirimsel (declarative) bir tanım** olmalı (JSON/YAML/TOML). Örnek alanlar:
   ```yaml
   id: win.gamedvr.background_recording.off
   title: "Arka planda oyun kaydını kapat"
   os: windows
   min_build: 22621
   risk: low            # low | medium | high
   requires_admin: false
   requires_reboot: false
   category: overlay
   evidence: "Arka plan kaydı GPU kodlayıcı ve disk kullanır"
   conflicts_with: []
   actions:
     - type: registry
       hive: HKCU
       key: Software\Microsoft\Windows\CurrentVersion\GameDVR
       name: AppCaptureEnabled
       value_type: DWORD
       value: 0
   detect:   # ayar zaten uygulanmış mı?
     ...
   ```
   Böylece Windows güncellemesi bir ayarı değiştirdiğinde sadece veritabanını güncellersin; build numarasına göre ayar gösterilir/gizlenir.
3. **Uygulayıcı (applier)** — Sadece birkaç ilkel işlem türünü bilir: registry yaz/sil, `powercfg` komutu, servis başlangıç türü, zamanlanmış görev aç/kapat, sysctl drop-in dosyası, systemd birimi. Her biri **"önce oku, kaydet, sonra yaz"** yapar.
4. **Geri alma günlüğü (undo journal)** — Kalıcı, insan tarafından okunabilir bir dosya (ör. `%ProgramData%\<Uygulama>\journal\2026-11-20T14-03-11.json`). Her kayıtta: eski değer, **değerin hiç olmadığı bilgisi** (yoksa geri alırken silmek gerekir — en sık yapılan hata), yeni değer, zaman, ayar kimliği, uygulama sürümü.
5. **Ayrıcalıklı yardımcı (privileged helper)** — Arayüz normal kullanıcı yetkisiyle çalışmalı; yönetici gerektiren işlemler ayrı, küçük, imzalı bir servis/yardımcıya (Windows Service, Linux'ta polkit ile `pkexec` yardımcısı, macOS'ta `SMAppService` daemon) gönderilmeli. Yardımcı sadece veritabanındaki tanımlı işlemleri kabul etmeli, rastgele komut çalıştırmamalı.
6. **Oturum modu (session mode)** — Bazı ayarlar kalıcı değil, **oyun açıkken** geçerli olmalı: güç planını "Yüksek performans"a almak, bildirimleri susturmak, belirli arka plan uygulamalarını duraklatmak, Windows Update'in o sırada indirme yapmasını ertelemek. Oyun süreci kapanınca otomatik geri alınır. Bu, kalıcı değişiklikten çok daha güvenlidir. (Linux'taki Feral GameMode tam olarak bu modeli kullanır.)
7. **Ölçüm modülü** — Önce/sonra karşılaştırması. Windows'ta **PresentMon** (Intel'in açık kaynak frame-time aracı; ETW tabanlı) entegrasyonu; Linux'ta **MangoHud** log'u. Ortalama FPS yerine **1% ve 0.1% low** ile frame-time grafiği göster. Kullanıcı "bu ayar gerçekten işe yaradı mı?" sorusunun cevabını görmeli.
8. **Kayma tespiti (drift detection)** — Windows güncellemeleri ve sürücü kurulumları ayarları sıfırlayabilir. Program açılışta günlükteki ayarların hâlâ geçerli olup olmadığını kontrol edip bildirmeli (otomatik tekrar uygulamadan önce sormalı).

### 2.3 Teknoloji önerisi

| Seçenek | Artı | Eksi |
|---|---|---|
| **C# / .NET 8+ + WinUI 3 veya WPF** (Windows odaklı) | Windows API'lerine (WMI, registry, servisler, ETW) en kolay erişim; PresentMon zaten ETW kullanıyor | Linux/macOS için ayrı arayüz gerekir (Avalonia ile çözülebilir) |
| **Rust çekirdek + Tauri arayüz** (çok platformlu) | Tek kod tabanı, küçük ikili dosya, bellek güvenli, `windows-rs` ile tüm Win32 API'leri | Öğrenme eğrisi daha dik |
| Python + PyQt | Hızlı prototip | Dağıtımı zor, antivirüs yanlış pozitifleri çok, yönetici yardımcısı için uygun değil |

**Öneri:** Önce Windows'u hedefle (oyuncuların büyük çoğunluğu orada). Çekirdek mantığı (tweak veritabanı, journal, kural motoru) platformdan bağımsız bir katmanda yaz; platforma özel "applier"ları ayrı modüller yap. Çok platform hedefliyorsan Rust + Tauri, sadece Windows ise .NET + WinUI.

### 2.4 Dağıtım ve güven

- **Kod imzalama sertifikası** al (OV/EV ya da Azure Trusted Signing). İmzasız, registry değiştiren, yönetici isteyen bir program Defender/SmartScreen ve antivirüslerden yanlış pozitif yer; kullanıcı güveni de düşer.
- Kod karartma (obfuscation), paketleyici (packer), gizli indirme yapma — hepsi antivirüs alarmı demek.
- **Çekirdek sürücüsü (kernel driver) yazma.** Gerekmez, risklidir, imzalatmak zordur ve anti-cheat sistemleriyle çakışır.
- Oyun süreçlerine **DLL enjekte etme, bellek okuma/yazma, overlay hook'u yapma** — anti-cheat (Vanguard, EAC, BattlEye, FACEIT, Javelin) bunu hile olarak algılayıp kullanıcıyı banlatabilir. Sadece işletim sistemi seviyesinde, oyunun dışından çalış. Süreç önceliği/afinite değiştirmek bile bazı anti-cheat'lerde engellenir; hata alırsan zorlamadan geç.
- Telemetri yoksa açıkça söyle; varsa açık onaylı (opt-in) olsun.

---

## 3. Bilgisayarı bozmadan uygulamanın kuralları (en kritik bölüm)

1. **Varsayılan: hiçbir şey yapma.** Program ilk açılışta sadece tarar ve öneri listesi sunar. Toplu "Hepsini uygula" düğmesi sadece *düşük riskli* ayarları kapsamalı.
2. **Her ayar için risk etiketi ve açıklama.** Kullanıcı ne yaptığını, neden yaptığını, olası yan etkisini görmeli ("Memory Integrity'yi kapatmak bazı oyunlarda %5–15 FPS kazandırabilir ama sistemi kötü amaçlı sürücülere karşı korumasız bırakır").
3. **Geri alınamayan hiçbir işlem yapma.** Dosya silme, uygulama kaldırma (debloat), sürücü silme, bölüm/önyükleyici değişikliği yok. Her şey journal üzerinden geri alınabilir olmalı.
4. **Üç katmanlı yedek:**
   - **Kendi journal'ın** (birincil, ayar bazında geri alma).
   - **Sistem Geri Yükleme noktası** (Windows) — `Checkpoint-Computer` veya `SRSetRestorePoint` API'si. Dikkat: Windows 11'de Sistem Koruması çoğu zaman **kapalı gelir**; açmak için kullanıcıdan onay al. Ayrıca Windows varsayılan olarak 24 saatte bir geri yükleme noktasına izin verir (`SystemRestorePointCreationFrequency`, varsayılan 1440 dk); bunu hesaba kat, başarısız olursa kullanıcıya söyle.
   - **Registry export** — değiştirilen her anahtarın `.reg` yedeği (journal bozulursa elle geri yüklenebilsin).
   - Linux'ta: Btrfs/Snapper veya Timeshift anlık görüntüsü varsa onu tetikle; yoksa journal + değiştirilen dosyaların kopyası.
5. **Yeniden başlatma gerektiren ayarlarda "onay zamanlayıcısı".** Ekran çözünürlüğü değiştirince "15 saniye içinde onaylamazsanız geri alınacak" diyen mantığın aynısı: yeniden başlatma sonrası program açılır, kullanıcı "Her şey yolunda" demezse veya sistem art arda başarısız açılırsa ayarlar otomatik geri alınır. Bir başlangıç görevi + "bekleyen değişiklik" bayrağıyla yapılır.
6. **Tek seferde az değişiklik, ölçerek.** Sorun çıkarsa hangi ayardan kaynaklandığı bilinsin. "Profil" olarak gruplanmış ayarlar bile tek tek geri alınabilmeli.
7. **Güvenlik özelliklerine dokunurken ekstra kapı.** Windows Defender, Windows Update, Güvenlik Duvarı, UAC, Secure Boot, TPM: **asla kapatma**. Memory Integrity/VBS: sadece "Gelişmiş" bölümde, açık uyarıyla ve kullanıcının ayrıca onayıyla.
8. **Anti-cheat uyumluluğu.** Battlefield 6 (Javelin), Valorant (Vanguard), FACEIT gibi sistemler Secure Boot / TPM / güvenlik durumunu kontrol ediyor; Microsoft da uzaktan doğrulama (attestation) tarafına gidiyor. Güvenlik ayarlarını kapatmak kullanıcının bazı oyunları **hiç açamamasına** yol açabilir. Program bunu bilmeli ve uyarmalı.
9. **Dizüstü ve masaüstüne farklı davran.** Dizüstünde "Ultimate Performance" + çekirdek park kapatma pil ve ısı felaketi olabilir; sadece şarjdayken ve oturum modunda uygula.
10. **Build/sürüm kontrolü.** Ayar veritabanında her ayar hangi Windows build'inde, hangi kernel sürümünde geçerli olduğunu bilsin. Tanınmayan sürümde riskli ayarlar gizlensin.
11. **Kaldırma (uninstall) her şeyi geri almalı** — ya da en azından kaldırma sırasında "Değişiklikleri geri al" seçeneği sunmalı.
12. **Hata durumunda dur.** Bir adım başarısız olursa o oturumda yapılan tüm değişiklikleri geri al (transaction mantığı).

---

## 4. Windows 11 — ayar kataloğu

### 4.1 Güncel bağlam (Kasım 2026)

- Windows 11 25H2, VBS/Memory Integrity kapalıyken yapılan testlerde oyunlarda Windows 10'u hafifçe geçiyor (TechSpot).
- **Memory Integrity (HVCI)**: Microsoft, Ekim 2026'dan itibaren kalite güncellemeleriyle uygun eski PC'lerde de otomatik açmaya başladı (Windows Latest, Tom's Hardware, PCWorld). CPU-sınırlı oyunlarda performans kaybı raporları %5'ten %15'e kadar değişiyor; PCWorld'ün eski testinde çoğu oyunda fark yoktu. Daha önce elle kapatılmış PC'lerde tekrar açılmadığı bildiriliyor. → Programın bunu **tespit etmesi** ve kullanıcıya "güncelleme bunu açtı" diye bildirmesi çok değerli bir özellik olur.
- **Timer resolution**: Microsoft dokümanına göre Windows 10 2004'ten beri `timeBeginPeriod` artık global değil, süreç bazında; Windows 11'de görünmeyen/simge durumundaki pencerelerin isteği garanti edilmiyor. Global davranışı geri getiren `GlobalTimerResolutionRequests` ayarı resmî değil ve niş. → Ekleme ya da sadece "deneysel" olarak sun.

### 4.2 Düşük risk (varsayılan önerilebilir)

| Ayar | Nasıl | Not |
|---|---|---|
| **Monitör yenileme hızı doğru mu?** | `EnumDisplaySettingsEx` ile desteklenen en yüksek hızı bul, aktifle karşılaştır; `ChangeDisplaySettingsEx` ile değiştir (onay zamanlayıcısıyla) | En sık ve en büyük kazanç. Kablo (HDMI 1.4 vs DP) sınırını da açıkla. |
| **Oyun doğru GPU'da mı?** (hibrit dizüstü) | `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` → değer adı: exe yolu, veri: `GpuPreference=2;` | Ayarlar > Ekran > Grafik ekranıyla aynı mekanizma. |
| **Oyun Modu** | `HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled` | **Denetimle düzeltildi (bkz. §8):** Genel öneri değil. Yalnızca çift CCD'li Ryzen 9 X3D'de gerekli. |
| **Arka plan oyun kaydı (Game DVR) kapalı** | `HKCU\System\GameConfigStore\GameDVR_Enabled = 0`, `HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\AppCaptureEnabled = 0` | Kullanıcı kayıt özelliğini kullanıyorsa sor. |
| **Pencereli oyunlar için iyileştirmeler** | `HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings` içine `SwapEffectUpgradeEnable=1;` | Eski DX10/11 pencereli oyunlarda gecikmeyi azaltır, VRR/Auto HDR'ı mümkün kılar. |
| **Değişken Yenileme Hızı (VRR)** | Aynı `DirectXUserGlobalSettings` içinde `VRROptimizeEnable=1;` | Uyumlu monitör varsa. |
| **Güç planı / güç modu** | Masaüstü: `powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c` (Yüksek performans). Modern Standby'lı dizüstülerde sadece "Dengeli" plan vardır; onun yerine Güç modu "En iyi performans" ayarlanır (`PowerSetActiveOverlayScheme`). | Tercihen **oturum modunda**. Önceki planın GUID'ini journal'a yaz. |
| **Başlangıç uygulamaları** | Görev Yöneticisi'nin kullandığı mekanizma: `...\Explorer\StartupApproved\Run` ikili değerleri | Silme değil, devre dışı bırakma. Kullanıcıya liste göster, kendisi seçsin. |
| **İşaretçi hassasiyetini artır (fare ivmesi)** | `SystemParametersInfo(SPI_SETMOUSE)` / `HKCU\Control Panel\Mouse` | Nişancı oyunlarında tutarlılık için; kullanıcı tercihi. |
| **Bildirimler / Odaklanma** | Oyun süresince "Rahatsız Etme" | Oturum modu. |
| **Windows Update oyun sırasında** | Etkin saatleri ayarla; oyun sırasında yeniden başlatma olmasın | Update'i **kapatma**. |
| **Sürücü kontrolü** | GPU sürücü sürüm/tarihini oku, üretici sayfasına yönlendir | Sürücüyü kendin indirip kurma (indirme kaynağı/güven sorunu); NVIDIA App / AMD Adrenalin / Intel'e yönlendir. |
| **Disk** | Oyun diskinde boş alan, SSD'de TRIM açık mı (`fsutil behavior query DisableDeleteNotify`), oyun HDD'de mi? | Sadece tespit ve öneri. |
| **Termal / throttling tespiti** | Oyun sırasında saat hızı ve sıcaklık izleme | Toz, termal macun, dizüstü soğutucu önerisi. Çoğu "düşük FPS" şikâyetinin kaynağı. |

### 4.3 Orta risk (açıklama + onay ile)

| Ayar | Nasıl | Risk |
|---|---|---|
| **HAGS (Donanım hızlandırmalı GPU zamanlaması)** | `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode` = 2 (açık) / 1 (kapalı), yeniden başlatma gerekir | DLSS Frame Generation için **gerekli**. Bazı eski sistem/sürücülerde takılma yapabilir — A/B test et. |
| **Memory Integrity (HVCI) kapatma** | `HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity\Enabled = 0`, yeniden başlatma | **Güvenlik kaybı.** Sadece "Gelişmiş", ayrı onay, kalıcı uyarı rozeti. Anti-cheat'lerin bu yöne kaydığını belirt. Tüm VBS'i kapatmak (hypervisor) WSL2, Hyper-V, Windows Sandbox, bazı anti-cheat'leri etkiler — önerme. |
| **CPU çekirdek park etme / minimum işlemci durumu** | `powercfg /setacvalueindex` ile güç planı alt ayarları | Masaüstünde makul, dizüstünde ısı/pil. Oturum modunda. |
| **Güç kısıtlaması (Power Throttling)** | Süreç bazında `SetProcessInformation(ProcessPowerThrottling)` veya güç planı | Global kapatma yerine sadece oyun süreci için. |
| **USB seçici askıya alma** | Güç planı alt ayarı | Kablosuz fare/kulaklık kopmaları için; pil etkisi. |
| **Arka plan uygulamalarını oyun sırasında duraklatma** | Tarayıcı, güncelleyiciler, senkronizasyon araçları | Kullanıcı tanımlı liste; zorla öldürme değil, sormalı. |
| **Süreç önceliği** | `SetPriorityClass(HIGH_PRIORITY_CLASS)` | `REALTIME` **asla** (sistemi kilitleyebilir). Anti-cheat erişimi engelleyebilir. |

### 4.4 Yapma (yüksek risk / placebo)

- Windows Defender'ı, Windows Update'i, Güvenlik Duvarı'nı, UAC'yi kapatmak.
- Secure Boot / TPM'i kapatmaya yönlendirmek (bazı oyunlar açılmaz).
- **Toplu servis kapatma / "debloat" betikleri**: Microsoft Store, Xbox uygulaması, Game Pass, yazdırma, Bluetooth, Windows Hello, güncellemeler bozulur. Kaldırılan sistem uygulamalarını geri getirmek zordur.
- Sayfa dosyasını (page file) kapatmak — bellek yetmeyince oyunlar çöker.
- Spectre/Meltdown azaltımlarını kapatmak (`FeatureSettingsOverride`) — güvenlik açığı.
- `bcdedit /set useplatformclock`, `disabledynamictick`, HPET oynamaları — modern sistemlerde çoğunlukla zararlı veya etkisiz; önyükleme ayarıdır.
- MSI (Message Signaled Interrupts) modu / kesme afinitesi değiştirmek — yanlış cihazda açılış hatası (boot loop).
- "RAM temizleyici", standby list boşaltma — modern Windows'ta etkisi yok ya da olumsuz.
- TCP/Nagle "ping düşürücü" ayarları — modern oyunların çoğu zaten `TCP_NODELAY` ya da UDP kullanır.
- Otomatik hız aşırtma (overclock/undervolt) — donanım ve garanti riski; üretici araçlarına bırak.
- Sürücüleri silip "temiz kurulum" otomasyonu (DDU benzeri) — yanlış giderse görüntü yok.

---

### 4.5 Arka plan uygulamaları, hizmetler, görsel efektler, Defender — kanıt özeti (20 Kasım 2026)

| Konu | Kanıt | Programın tutumu |
|---|---|---|
| **Hizmet kapatma / debloat (Tiny11, betikler)** | Normal Windows 11 ile karşılaştırmalarda FPS farkı birkaç kare, çoğu zaman ölçüm hatası içinde; kazanç boştayken birkaç yüz MB–1 GB RAM. Sürücü, zamanlayıcı ve grafik altyapısı aynı kalıyor. Betiklerdeki asıl etkili adım genellikle VBS/HVCI'ı kapatmak. Bedeli: Store/Xbox/Bluetooth/yazdırma/güncellemelerin bozulması, güncellemelerin bazı hizmetleri geri açması. | Toplu hizmet kapatma **yok**. VBS/HVCI ayrı bir kural olarak zaten gösteriliyor. |
| **Görsel efektler, saydamlık, animasyonlar** | Kontrollü FPS testi bulunamadı. Tam ekran oyunlarda masaüstü bileşeni (DWM) büyük ölçüde devre dışı; pencereli modda yükü harici GPU için çok küçük. Fayda zayıf dahili grafikli eski cihazlarda masaüstü akıcılığıyla sınırlı. | Performans ayarı olarak sunulmaz; ileride yalnızca zayıf iGPU'lu dizüstülerde bilgi olarak gösterilebilir. |
| **Windows Defender** | Eski testlerde ortalama FPS'e etkisi yok denecek kadar az; asıl risk dosya açılırken tarama kaynaklı anlık takılmalar (1% low). 2022'de bazı Intel sistemlerde ~%6 işlemci kaybı bildirildi (sonraki durumu doğrulanamadı). | Defender'ı kapatma **yok**. İleride (yönetici yardımcısıyla) oyun kütüphanesi klasörü için tarama istisnası, güvenlik bedeli açıkça yazılarak "orta risk" seçeneği olabilir. |
| **Aktif arka plan uygulamaları** (Discord katmanı/yayını, video oynatan tarayıcı, OneDrive senkronizasyonu, güncelleme indiren başlatıcılar, RGB/üretici yazılımları) | Etkisi büyük olabiliyor (özellikle 4 çekirdekli işlemcilerde), ama kanıtlar çoğunlukla kullanıcı deneyimi ve sistemden sisteme çok değişiyor. Genel bir "şunları kapat" listesi doğru değil. | **Faz 3:** Oyun sırasında süreç bazında ölçüm; o sistemde gerçekten kaynak tüketenleri göster, seçilenleri oyun süresince duraklat. |

Kaynaklar: [TechYorker — Tiny11](https://techyorker.com/tiny11-hands-on-how-much-lighter-is-windows-11-without-the-extra-bloat/), [Brutal Optimizer — debloat betikleri](https://brutaloptimizer.com/blog/are-windows-debloat-scripts-safe-tiny11-atlas), [tiny11-automated](https://github.com/kelexine/tiny11-automated), [Chillblast — antivirüs ve oyun](https://www.chillblast.com/blog/does-having-anti-virus-installed-slow-down-games), [TechPowerUp — Defender ve Intel](https://techpowerup.com/295877/windows-defender-can-significantly-impact-intel-cpu-performance-we-have-the-fix), [Windows Central — görsel efektler](https://windowscentral.com/how-disable-visual-effects-speed-windows-11), [Deadlock forumu — Discord takılması](https://forums.playdeadlock.com/threads/running-discord-on-a-4-core-cpu-causes-stutters-in-deadlock.94387).

## 5. Linux — ayar kataloğu

Linux'ta oyun performansının çoğu **güncel çekirdek + güncel Mesa (AMD/Intel) veya NVIDIA sürücüsü + güncel Proton** üçlüsünden gelir. Program önce bunları kontrol etmeli. Dağıtım çeşitliliği büyük olduğundan paket yöneticisi (apt/dnf/pacman/zypper) ve init sistemi (systemd) tespiti şart.

### 5.1 Düşük risk

| Ayar | Nasıl | Not |
|---|---|---|
| **Feral GameMode** | Paket kurulu mu, `gamemoded` çalışıyor mu kontrol; Steam başlatma seçeneğine `gamemoderun %command%` önerisi | Oyun sırasında CPU governor, I/O önceliği, GPU performans seviyesini ayarlar ve **otomatik geri alır** — oturum modunun hazır hâli. |
| **Güç profili** | `powerprofilesctl set performance` veya `tuned-adm profile` | Oturum modunda; dizüstünde dikkat. |
| **MangoHud** | FPS/frame-time ölçümü ve log | Ölçüm modülün için ideal. |
| **Proton sürümü** | Steam'in Proton'u / Proton-GE önerisi | Oyun bazında. |
| **vm.max_map_count** | `/etc/sysctl.d/99-<app>.conf` içinde `vm.max_map_count = 1048576` | Fedora, Ubuntu ve Arch artık bunu varsayılan yapıyor; sadece düşükse uygula. Bazı oyunlar düşük değerde çöker. |
| **VRR / yenileme hızı** | Wayland compositor (KDE/GNOME) ayarı, `xrandr` | Windows'taki gibi en büyük kazançlardan biri. |
| **ZRAM** | Az RAM'li sistemlerde `zram-generator` | Swap'ın yerine; kurulu değilse öner. |
| **Gamescope** | Steam Deck'teki mikro-compositor; ölçekleme ve frame limiti | Öneri seviyesinde. |

### 5.2 Orta risk

| Ayar | Nasıl | Not |
|---|---|---|
| **sched_ext zamanlayıcıları** (ör. `scx_lavd`) | Çekirdek 6.12+; `scx` paketi ve `scx_loader`/systemd birimi | Igalia'nın oyun iş yükleri için geliştirdiği LAVD; çökerse çekirdek otomatik olarak varsayılan zamanlayıcıya döner (sched_ext'in güvenlik tasarımı). Yine de deneysel olarak sun. |
| **split_lock_mitigate** | `kernel.split_lock_mitigate = 0` (çekirdek 6.2+) | Bazı Proton oyunlarında Intel'de takılmayı giderir; teorik DoS koruması kaybı. |
| **Şeffaf büyük sayfalar (THP)** | `/sys/kernel/mm/transparent_hugepage/enabled` | Oyuna göre değişir; ölçümle. |
| **NTSYNC** | Çekirdek 6.14+ `ntsync` modülü + destekleyen Wine/Proton | Windows senkronizasyon ilkellerini çekirdekte uygular; destek durumu dağıtıma göre değişir. |
| **Özel çekirdekler** (Zen, XanMod, CachyOS) | Sadece bilgi/öneri | Çekirdek değiştirmeyi programın kendisi yapmamalı — açılmayan sistem riski. |

### 5.3 Yapma

- Önyükleyici (GRUB/systemd-boot) satırlarını otomatik değiştirmek, özellikle `mitigations=off`.
- Çekirdek kurmak/kaldırmak, NVIDIA sürücüsünü kendi başına değiştirmek (siyah ekran riski).
- Doğrudan `/etc/sysctl.conf`'u düzenlemek — her zaman **kendi drop-in dosyan** (`/etc/sysctl.d/99-<app>.conf`) olsun; geri almak = dosyayı silmek.
- SELinux/AppArmor kapatmak.

**Linux'ta geri alınabilirlik pratiği:** Tüm değişiklikler kendi adınla işaretlenmiş drop-in dosyaları ve systemd birimleri olarak yazılsın. Kaldırmak = o dosyaları silip `sysctl --system` / `systemctl daemon-reload` çalıştırmak. Btrfs + Snapper/Timeshift varsa önce anlık görüntü al.

---

## 6. macOS — sınırlı alan

macOS'ta yazılımla yapılabilecek optimizasyon alanı çok dardır ve bu kasıtlıdır (SIP, imzalı sistem birimi).

- **Oyun Modu** otomatik devreye girer (tam ekran oyunlarda); Apple'a göre CPU/GPU önceliğini oyuna verir ve AirPods/kontrolcü Bluetooth örnekleme hızını iki katına çıkarır. Tahoe'da oyun bazlı ayarı Game Overlay'den, Sequoia/Sonoma'da Game Mode menüsünden açılıp kapatılır.
- **Düşük Güç Modu** kapalı mı (`pmset -g`)? Açıksa GPU'yu kısar.
- Ekran yenileme hızı (ProMotion/harici monitör), Spotlight dizinlemesinin bitip bitmediği, bellek baskısı (memory pressure), arka plan uygulamaları.
- Windows oyunları için CrossOver / Game Porting Toolkit / Whisky benzeri katmanlar — sadece bilgilendirme.
- **SIP'i kapattırmaya asla yönlendirme.**

→ macOS'ta program **teşhis + öneri + izleme** aracı olmalı, "ayar uygulayıcı" değil.

---

## 7. Önerilen yol haritası (MVP → olgun ürün)

1. **Faz 1 — Teşhis aracı (salt okuma).** ✅ Tamamlandı. Envanter + kural motoru + rapor. Sıcaklık/throttling izleme Faz 3'e kaldı.
2. **Faz 2 — Güvenli uygulayıcı.** ✅ Tamamlandı. Günlük + kayıt defteri/güç planı/ekran modu/fare yürütücüleri + doğrulama + geri alma + 15 sn onaylı ekran değişikliği. Yönetici gerektiren ayarlar ve Sistem Geri Yükleme noktası Faz 5'e kaldı.
3. **Faz 3 — Oyun oturumu: kaynak ölçümü + oturum modu.** ✅ İlk sürüm tamamlandı (§4.5'teki kanıta dayanır). Sıcaklık okunamıyor (yönetici/sürücü gerektiriyor); onun yerine Windows'un "% Performance Limit" sayacıyla güç/ısı sınırına takılma ölçülüyor. Oturum sırasında güç planı değiştirme eklenmedi (Ryzen'de kanıtlanmış faydası yok).
   - **Oyun tespiti:** Steam/Epic kütüphanelerindeki çalıştırılabilir dosyalar + tam ekran/ön plan süreç izleme; kullanıcı elle oyun ekleyebilir.
   - **Oturum sırasında kaynak ölçümü:** Oyun açıkken birkaç saniyede bir süreç bazında işlemci (`GetProcessTimes`), ekran kartı (`GPU Engine` performans sayaçları), disk G/Ç ve bellek örneklenir. Oyun dışındaki en büyük tüketiciler oturum sonunda listelenir ("Oyun sırasında OneDrive diskin %40'ını, tarayıcı GPU'nun %8'ini kullandı").
   - **Sıcaklık/throttling:** Oyun sırasında işlemci saat hızı ve güç sınırı düşüşleri (yapılabildiği ölçüde yönetici gerektirmeden).
   - **Oturum modu:** Kullanıcının seçtiği uygulamalar oyun açılınca duraklatılır/kapatılır, isteğe bağlı olarak güç planı ve bildirimler oturum boyunca değiştirilir; oyun kapanınca hepsi geri alınır. Mevcut günlük (journal) altyapısını kullanır; çökme sonrası kurtarma aynı şekilde çalışır.
   - **Önerilen eylemler ölçüme dayanır:** Program hiçbir hizmeti ya da uygulamayı kendi listesinden kapatmaz; yalnızca o sistemde ölçülen tüketicileri gösterir, karar kullanıcınındır.
   - **Kapsam dışı:** Toplu hizmet kapatma, Defender'ı kapatma, görsel efektleri performans ayarı olarak sunma (§4.5).
4. **Faz 4 — FPS ölçümü.** ✅ İlk sürüm tamamlandı: PresentMon 2.6.0 ile etiketli ölçüm, oynaklığa göre karşılaştırma. Ayarı ölçümler arasında kullanıcı değiştiriyor; programın ayarı kendisi değiştirip A/B'yi otomatik yürüttüğü rehberli deney henüz yok. İlk plan: PresentMon entegrasyonu, önce/sonra karşılaştırma ekranı (ortalama, 1% ve 0.1% low). Belirsiz ayarlar (Oyun Modu, HAGS, Discord donanım hızlandırma vb.) kullanıcının kendi sisteminde A/B test edilir.
5. **Faz 5 — Gelişmiş ayarlar.** ✅ İlk sürüm tamamlandı: HAGS, HVCI, Defender oyun klasörü istisnası, başlangıç programları; işlem başına UAC ile çalışan, izin listesiyle sınırlı yardımcı; Sistem Geri Yükleme noktası; yeniden başlatma sonrası zaman aşımlı onay; kayma tespiti. Gerçek yönetici işlemleri (UAC onayı gerektirdiği için) otomatik testlerde değil, kullanıcı tarafından denenmeli.
6. **Faz 6 — Linux desteği.** ✅ İlk sürüm tamamlandı: uygulama ve CLI platform arayüzü (`IPlatformServices`) arkasında; Linux teşhis kuralları, sysctl drop-in ve güç profili değişiklikleri, izin listeli pkexec yardımcısı, /proc tabanlı oturum ölçümü, MangoHud kayıt içe aktarma. Docker'da gerçek Linux çekirdeğinde test edildi; pkexec ve masaüstü arayüzü gerçek bir Linux masaüstünde henüz denenmedi. Oyun sırasında uygulama kısma Linux'ta bilinçli olarak yok (RLIMIT_NICE nedeniyle geri alınamaz).
7. **Test altyapısı:** Her ayarı Hyper-V/VirtualBox sanal makinelerde (farklı Windows build'leri) uygula-geri al döngüsüyle otomatik test et; "uygula → geri al → sistem durumu ilk hâliyle bire bir aynı mı?" kontrolü. Gerçek donanımda en az bir masaüstü + bir hibrit GPU'lu dizüstü test et.

---

## 8. Kural denetimi (20 Kasım 2026)

İlk sürümdeki teşhis kuralları genel tavsiyelere dayanıyordu. Her kuralın iddiası kaynaklarla karşılaştırıldı; sonuç:

| Kural | Önceki iddia | Kanıt | Karar |
|---|---|---|---|
| Oyun Modu | Kapalıysa "öneri", açılmalı | Windows 11'de ortalama FPS'e etkisi ~±%5; bazı işlemci yoğun oyunlarda kapalıyken 1% low daha iyi. 2020'deki Windows 10 takılma hatası (AMD ve NVIDIA) Microsoft tarafından düzeltildi. Çift CCD'li Ryzen 9 X3D'de AMD'nin V-Cache sürücüsü çekirdek park etmek için Oyun Modu'na ihtiyaç duyuyor. | Yalnızca 7900X3D/7950X3D/9900X3D/9950X3D'de kapalıysa **uyarı** + düzeltme; diğerlerinde **bilgi**, düzeltme önerilmez. |
| HAGS | Kapalıysa tüm harici GPU'larda öneri, "gecikmeyi azaltır" | Testlerde FPS neredeyse aynı. DLSS Kare Oluşturma için zorunlu; AMD AFMF için değil. | Yalnızca NVIDIA harici GPU'da öneri; "gecikmeyi azaltır" iddiası kaldırıldı. |
| Arka plan kaydı | Etki: orta | Ölçümler küçük maliyet gösteriyor (eski bir testte <2 FPS, tahminler %3–6). Microsoft performans sorununda ilk kapatılacak ayar olarak gösteriyor. | Öneri kalıyor, etki **düşük**e indirildi. |
| RAM hızı | 4800 MT/s üstü her DDR5 için "XMP/EXPO etkin görünüyor" | DDR5'in JEDEC hızı modüle göre 4800/5200/5600 vb. olabilir. | Profil etkin yalnızca yapılandırılmış hız modülün kendi taban hızını aşınca söyleniyor; aksi hâlde "standart hızda" bilgisi. |
| Sürücü yaşı | >1 yıl uyarı, >6 ay öneri | Eski sürücü ölçülmüş bir yavaşlama değil, yeni oyunlara yönelik düzeltmelerin eksikliği. | >1 yıl öneri, >6 ay bilgi. |
| Secure Boot | Etki: yüksek | Performansa etkisi yok; Valorant, Battlefield 6, Black Ops 7 açılmıyor. FACEIT örneği doğrulanamadı. | Performans etkisi **yok** olarak işaretlendi, oyun listesi doğrulanmış örneklerle değiştirildi. |
| Pencereli oyun iyileştirmeleri | Öneri | Microsoft'un kendi belgesi: DX10/11 pencereli oyunlarda flip modeline geçiş, daha düşük gecikme, VRR/Auto HDR. Bazı oyunlarda yırtılma bildirildi. | Öneri kalıyor, yırtılma uyarısı eklendi. |
| Pil, disk alanı, HDD, başlangıç | Kesin ifadeler ("yarı güce düşer", "takılmalar başlar") | Genel olarak doğru ama oranlar modele/oyuna bağlı. | İfadeler yumuşatıldı, sınıflandırma aynı. |
| Yenileme hızı, monitörün anakarta bağlı olması, hibrit GPU, tek kanal RAM, Bellek Bütünlüğü | — | Doğrudan donanım gerçeği ya da önceden kaynaklı. | Değişiklik yok. |

Ek kaynaklar: [MakeUseOf — Oyun Modu testi](https://www.makeuseof.com/i-tested-windows-game-mode-for-month-what-benchmarks-actually-showed/), [WindowsForum — Oyun Modu ve işlemci darboğazı](https://windowsforum.com/windows-news.4/windows-11-game-mode-can-hurt-cpu-bound-fps-in-some-games.441277/), [PC Gamer — Windows 10 Oyun Modu hata düzeltmesi](https://pcgamer.com/microsoft-says-its-fixed-an-issue-in-windows-10-game-mode-that-results-in-lower-frame-rates), [TechSpot — 9950X3D incelemesi](https://www.techspot.com/review/2965-amd-ryzen-9-9950X3D/), [Tom's Hardware — HAGS testi](https://www.tomshardware.com/news/gpu-hardware-scheduling-tested), [PCWorld — Game Bar incelemesi](https://www.pcworld.com/article/3315624/gaming/game-bar-for-windows-10-review.html), [Xbox Wire — Game DVR optimizasyonu](https://news.xbox.com/en-us/?p=176295), [Microsoft — Pencereli oyun iyileştirmeleri](https://support.microsoft.com/en-us/topic/3f006843-2c7e-4ed0-9a5e-f9389e535952), [PC Gamer — Battlefield 6 Secure Boot](https://www.pcgamer.com/hardware/battlefield-6-requires-secure-boot-to-run-and-its-coming-to-more-games-soon-including-call-of-duty-black-ops-7-heres-what-it-is-why-linux-gamers-are-mad-about-it-and-how-to-enable-it), [Evetech — DDR5 JEDEC hızları](https://evezone.evetech.co.za/quick-bytes/why-is-ddr5-memory-not-running-at-its-advertised-speed).

**İlke:** Bir kural ancak ölçülmüş ya da üretici tarafından belgelenmiş bir etkiye dayanıyorsa "öneri" veya "uyarı" verir; aksi hâlde "bilgi" olarak kalır ve otomatik düzeltme önerilmez.

## 9. Kaynaklar

- [TechSpot — Windows 11 vs Windows 10 oyun performansı (25H2)](https://www.techspot.com/article/3081-windows-11-vs-windows-10-gaming/)
- [PC Games Hardware — 25H2 oyun benchmark'ları](https://www.pcgameshardware.de/Windows-Software-277633/News/25H-in-Spiele-Benchmarks-1493377/)
- [TechRadar — 25H2, Windows 10'u geçiyor](https://www.techradar.com/computing/windows/windows-11-25h2-edges-ahead-of-windows-10-in-gaming-performance-testing-proves-newer-os-is-faster-but-theres-an-elephant-sized-bsod-in-the-room)
- [PCWorld — Memory Integrity performans testi](https://www.pcworld.com/article/541137/tested-does-windows-11-nuke-gaming-performance.html)
- [Windows Latest — Memory Integrity zorunlu açılıyor (Kasım 2026)](https://www.windowslatest.com/2026/11/11/windows-11-is-now-force-enabling-memory-integrity-a-feature-microsoft-admitted-can-hurt-gaming-performance-and-heres-how-to-turn-it-off/)
- [Tom's Hardware — Memory Integrity'nin daha fazla PC'ye yayılması](https://www.tomshardware.com/software/windows/microsoft-will-expand-windows-11-memory-integrity-feature-to-more-pcs-starting-in-october-security-feature-reduces-gaming-performance-on-some-systems)
- [PCWorld — Ekim 2026 güvenlik değişikliği ve oyunlar](https://www.pcworld.com/article/3227631/windows-11-will-be-more-secure-from-october-but-gamers-could-suffer-as-a-result.html)
- [Microsoft Destek — Memory Integrity ve sürücü uyumluluğu](https://support.microsoft.com/en-us/topic/a255f612-2949-4373-a566-ff6f3f474613)
- [FACEIT — Windows güvenlik gereksinimleri](https://support.faceit.com/hc/en-us/articles/23117181142556)
- [TechSpot — Battlefield anti-cheat Secure Boot gereksinimi](https://www.techspot.com/community/topics/ea-updates-battlefield-2024-anti-cheat-mitigation-to-require-windows-secure-boot.292627)
- [PC Gamer — Microsoft'un uzaktan doğrulama (attestation) yönü](https://www.pcgamer.com/software/microsofts-updated-anti-cheating-measures-go-far-beyond-secure-boot-including-remote-attestation-for-online-system-verification)
- [Microsoft Learn — timeBeginPeriod](https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod)
- [PCGamingWiki — GlobalTimerResolutionRequests](https://community.pcgamingwiki.com/files/file/2563-global-timer-resolution-requests-windows-11)
- [Igalia / Changwoo Min — scx_lavd sunumu (2026)](https://www.liglab.fr/sites/default/files/Mediatheque/scx_lavd-inria2026.pdf)
- [jisaku.com — Linux Proton rehberi (2026)](https://jisaku.com/posts/linux-gaming-proton-guide)
- [Apple Destek — Oyun Modu](https://support.apple.com/105118)
- [macos-tahoe.com — Tahoe oyun performansı sorunları (üçüncü taraf)](https://macos-tahoe.com/blog/macos-tahoe-gaming-performance-issues-fix-guide-2026/)

> Not: Registry yolları, GUID'ler ve çekirdek sürüm eşikleri bilinen değerlerdir ama her biri uygulamaya alınmadan önce hedef build üzerinde sanal makinede doğrulanmalıdır; Microsoft bu ayarların bir kısmını belgelemez ve sürümler arasında değiştirebilir.
