using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq; // Ez a 'using' szükséges a .ToListAsync()-hoz!
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;                 // Fájlműveletekhez (Path, File, Directory)
using System.Diagnostics;        // A Process osztályhoz (FFmpeg futtatása)
using System.Text.RegularExpressions; // A Regex-hez (fájlnév tisztítás)
using System.Threading;          // OPTIMALIZÁLÁS: Hozzáadva a SemaphoreSlim-hez
using YoutubeExplode;            // Maga a letöltő
using YoutubeExplode.Videos.Streams; // A StreamInfo-hoz


namespace YoutubeMp3Downloader
{
    public partial class Form1 : Form
    {
        // ÚJ: Eltároljuk az aktuális videó stream-jeit, hogy ne kelljen kétszer lekérdezni
        private StreamManifest _currentManifest = null;
        private YoutubeClient _youtubeClient;
        private bool _isPlaylist = false; // ÚJ: Jelzi, ha a link playlist

        public Form1()
        {
            InitializeComponent();
            _youtubeClient = new YoutubeClient(); // Inicializáljuk a klienst

            // ==================================================================
            // JAVÍTÁS (2025.11.03): Ablak beállítások
            // ==================================================================
            this.Text = "Rezust YouTube Downloader"; // Ablak címének beállítása
            this.FormBorderStyle = FormBorderStyle.FixedSingle; // Nem átméretezhető
            this.MaximizeBox = false; // Maximalizálás gomb letiltása
            // ==================================================================


            // Induláskor beállítjuk az alap láthatatlanságot
            qualityLabel.Visible = false;
            qualityComboBox.Visible = false;
            thumbnailPreviewBox.Visible = false; // ÚJ: Thumbnail elrejtése

            // ==================================================================
            // JAVÍTÁS: A Designer-ben az 'Items' (Collection) LEGYEN ÜRES!
            // A kód fogja feltölteni, így elkerüljük a duplikációt.
            // ==================================================================
            formatComboBox.Items.Clear(); // Biztonsági törlés
            formatComboBox.Items.AddRange(new string[] { "MP3", "MP4" });

            // JAVÍTÁS: A 'Select' szöveg a Text tulajdonság, nem egy 'Item'
            formatComboBox.Text = "Select format...";

            // ==================================================================
            // JAVÍTÁS: Logika módosítás - Formátum választó letiltva URL nélkül
            // ==================================================================
            formatComboBox.Enabled = false;
            // Manuálisan bekötjük az URL mező 'TextChanged' eseményét
            this.urlTextBox.TextChanged += new System.EventHandler(this.urlTextBox_TextChanged);
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // Ez egy valószínűleg nem használt eseménykezelő,
            // (pl. egy véletlen dupla kattintás a Form-ra a Designerben)
            // de a Form1.Designer.cs hivatkozik rá. Maradhat üresen.
        }

        // JAVÍTÁS: Ez a metódus mostantól használatban van (létrehozva a 47. sorban)
        // JAVÍTÁS (2025.11.03): Kiegészítve a UI visszaállításával
        private void urlTextBox_TextChanged(object sender, EventArgs e)
        {
            // 1. Csak akkor engedélyezzük a formátum választást, ha van szöveg az URL mezőben
            formatComboBox.Enabled = !string.IsNullOrWhiteSpace(urlTextBox.Text);

            // 2. JAVÍTÁS: Ha a link változik, töröljük a formátum kiválasztását
            // és elrejtjük a minőség/thumbnail szekciót, hogy új szkennelést kényszerítsünk ki.
            formatComboBox.Text = "Select format...";
            qualityLabel.Visible = false;
            qualityComboBox.Visible = false;
            thumbnailPreviewBox.Visible = false;
            thumbnailPreviewBox.Image = null;

            // 3. Töröljük a gyorsítótárazott adatokat is
            _currentManifest = null;
            _isPlaylist = false;
        }

        private void urlLabel_Click(object sender, EventArgs e) { }

        // ==================================================================
        // JAVÍTÁS: Hozzáadva a hiányzó, Designer által generált eseménykezelők,
        // hogy a fordítás sikeres legyen.
        // ==================================================================
        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            // Ez egy valószínűleg nem használt eseménykezelő,
            // de a Form1.Designer.cs hivatkozik rá. Maradhat üresen.
        }

        private void statusLabel_Click(object sender, EventArgs e)
        {
            // Ez egy valószínűleg nem használt eseménykezelő,
            // (pl. egy véletlen dupla kattintás a label-re a Designerben)
            // de a Form1.Designer.cs hivatkozik rá. Maradhat üresen.
        }
        // ==================================================================

        private void browseButton_Click(object sender, EventArgs e)
        {
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                if (fbd.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
                {
                    folderTextBox.Text = fbd.SelectedPath;
                }
            }
        }


        // ==================================================================
        // ÚJ SEGÉDMETÓDUSOK a felület lezárásához és feloldásához
        // ==================================================================

        /// <summary>
        /// Lezárja a fő vezérlőket és "Töltés" állapotot jelez.
        /// </summary>
        private void LockUI(string statusMessage)
        {
            this.Invoke((Action)(() => {
                statusLabel.Text = statusMessage;
                progressBar.Style = ProgressBarStyle.Marquee; // Végtelenített "mozgó" csík
                downloadButton.Enabled = false;
                browseButton.Enabled = false;
                urlTextBox.Enabled = false;
                formatComboBox.Enabled = false;
                qualityComboBox.Enabled = false;
                thumbnailPreviewBox.Visible = false; // ÚJ: Lezáráskor elrejtjük
            }));
        }

        /// <summary>
        /// Feloldja a vezérlőket és visszaállítja az alaphelyzetet.
        /// </summary>
        private void UnlockUI()
        {
            this.Invoke((Action)(() => {
                statusLabel.Text = "Ready.";
                progressBar.Style = ProgressBarStyle.Blocks; // Normál csík
                progressBar.Value = 0;
                downloadButton.Enabled = true;
                browseButton.Enabled = true;
                urlTextBox.Enabled = true;
                formatComboBox.Enabled = true;
                qualityComboBox.Enabled = true;
            }));
        }

        // ==================================================================
        // JAVÍTÁS (2025.11.04): Új segédmetódus az FFmpeg útvonalának okos kereséséhez
        // ==================================================================
        private string GetFfmpegPath()
        {
            // 1. út: A 'bin' almappa (a tiszta Release mappához)
            string releasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "ffmpeg.exe");
            if (File.Exists(releasePath))
            {
                return releasePath;
            }

            // 2. út: A fő mappa (a Debug módhoz)
            string debugPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(debugPath))
            {
                return debugPath;
            }

            // Ha sehol nincs meg, az alapértelmezett (debug) útvonalat adjuk vissza,
            // hogy a ConvertToMp3Async hibaüzenete a legegyszerűbb útvonalat mutassa.
            return debugPath;
        }

        // ==================================================================

        // ÚJ METÓDUS: Csak a "Jó, Közepes, Alacsony" opciókat tölti be
        private void PopulateFallbackQualities()
        {
            qualityComboBox.Items.Clear();
            qualityComboBox.Items.AddRange(new string[] { "High", "Medium", "Low" }); // ANGOLRA CSERÉLVE
            qualityComboBox.SelectedIndex = 0;
        }

        // ==================================================================
        // ÚJ MENÜ FUNKCIÓK (A kérésed alapján)
        // ==================================================================

        // Ez a metódus neve, amit a VS Designer alapból generál a "How to Use" gombra
        private void howToUseToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // JAVÍTÁS: Ide kerül a Help szöveg
            string helpTitle = "How to Use";
            string helpMessage =
                "1. Paste a YouTube Video or Playlist URL into the 'URL' box.\n\n" +
                "2. Select a format (MP3 or MP4). The program will then scan for available qualities.\n\n" +
                "3. Select your desired quality from the 'Quality' dropdown.\n\n" +
                "4. Click 'Browse' to choose a folder to save your file(s).\n\n" +
                "5. Click 'Start Download'.\n\n" +
                "Note: MP3 conversion and high-quality MP4 (720p+) merging both require 'ffmpeg.exe' to be in the same folder as this program.";

            MessageBox.Show(helpMessage, helpTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Ez a metódus neve, amit a VS Designer alapból generál az "Open Download Location" gombra
        private void openDownloadLocationToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // JAVÍTÁS: Ide kerül a mappa megnyitásának logikája
            string path = folderTextBox.Text;

            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show("No download folder has been selected yet.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (Directory.Exists(path))
            {
                // Megnyitja a mappát a Windows Intézőben
                Process.Start("explorer.exe", path);
            }
            else
            {
                MessageBox.Show("The selected folder does not exist or has been moved.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ==================================================================


        // EZ AZ OPTIMALIZÁLT LETÖLTŐ METÓDUS
        private async void downloadButton_Click(object sender, EventArgs e)
        {
            // 1. Lépés: Olvassuk ki az adatokat a GUI-ból
            string inputUrl = urlTextBox.Text;
            string downloadFolder = folderTextBox.Text;
            string selectedFormat = formatComboBox.SelectedItem?.ToString();
            string selectedQuality = qualityComboBox.SelectedItem?.ToString();


            // 2. Lépés: Ellenőrzés (Validation)
            if (string.IsNullOrWhiteSpace(inputUrl) ||
                string.IsNullOrWhiteSpace(downloadFolder) ||
                string.IsNullOrWhiteSpace(selectedFormat) ||
                string.IsNullOrWhiteSpace(selectedQuality))
            {
                // ANGOLRA CSERÉLVE
                MessageBox.Show("All fields must be filled (URL, Folder, Format, Quality)!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // JAVÍTÁS: Ellenőrizzük, hogy érvényes formátum van-e kiválasztva,
            // és nem csak a "Select format..." szöveg
            if (selectedFormat != "MP3" && selectedFormat != "MP4")
            {
                MessageBox.Show("Please select a valid format (MP3 or MP4).", "Invalid Format", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 3. Lépés: UI letiltása
            LockUI("Preparing download..."); // ANGOLRA CSERÉLVE

            try
            {
                // 4. Lépés: YoutubeExplode inicializálása
                var videosToDownload = new List<YoutubeExplode.Videos.IVideo>();

                // Az URL ellenőrzése és a videók gyűjtése itt történik
                var playlistId = YoutubeExplode.Playlists.PlaylistId.TryParse(inputUrl);
                var videoId = YoutubeExplode.Videos.VideoId.TryParse(inputUrl);

                if (playlistId != null)
                {
                    this.Invoke((Action)(() => statusLabel.Text = "Fetching playlist data...")); // ANGOLRA CSERÉLVE
                    // JAVÍTÁS: Kicserélve C# 7.3 kompatibilis "await foreach"-ről C# 7.0 kompatibilis GetAsyncEnumerator-ra
                    var enumerator = _youtubeClient.Playlists.GetVideosAsync(playlistId.Value).GetAsyncEnumerator();
                    try
                    {
                        while (await enumerator.MoveNextAsync())
                        {
                            videosToDownload.Add(enumerator.Current);
                        }
                    }
                    finally
                    {
                        await enumerator.DisposeAsync();
                    }
                }
                else if (videoId != null)
                {
                    this.Invoke((Action)(() => statusLabel.Text = "Fetching video data...")); // ANGOLRA CSERÉLVE
                    var video = await _youtubeClient.Videos.GetAsync(videoId.Value);
                    videosToDownload.Add(video);
                }
                else
                {
                    // ANGOLRA CSERÉLVE
                    statusLabel.Text = "Error: Invalid YouTube URL.";
                    MessageBox.Show("The provided URL is not a valid YouTube video or playlist.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UnlockUI();
                    return;
                }

                this.Invoke((Action)(() => progressBar.Maximum = videosToDownload.Count));
                int completedCount = 0;

                if (videosToDownload.Count == 0)
                {
                    statusLabel.Text = "No video found."; // ANGOLRA CSERÉLVE
                    UnlockUI();
                    return;
                }

                // 5. Lépés: Párhuzamosság beállítása
                const int MAX_PARALLEL_TASKS = 4;
                var semaphore = new SemaphoreSlim(MAX_PARALLEL_TASKS);

                var tasks = new List<Task>();
                this.Invoke((Action)(() => {
                    statusLabel.Text = "Preparing downloads..."; // ANGOLRA CSERÉLVE
                    progressBar.Style = ProgressBarStyle.Blocks;
                }));


                // 6. Lépés: Feladatok (Task-ok) létrehozása
                foreach (var video in videosToDownload)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            string safeTitle = SanitizeFileName(video.Title);

                            this.Invoke((Action)(() => {
                                statusLabel.Text = $"Processing: {safeTitle}"; // ANGOLRA CSERÉLVE
                            }));

                            // Döntés a formátum alapján
                            if (selectedFormat == "MP3")
                            {
                                await DownloadAsMp3Async(_youtubeClient, video, downloadFolder, safeTitle, selectedQuality);
                            }
                            else // "MP4"
                            {
                                await DownloadAsMp4Async(_youtubeClient, video, downloadFolder, safeTitle, selectedQuality);
                            }

                            int currentCount = Interlocked.Increment(ref completedCount);

                            this.Invoke((Action)(() => {
                                progressBar.Value = currentCount;
                                // ANGOLRA CSERÉLVE
                                statusLabel.Text = $"({currentCount}/{videosToDownload.Count}) Done: {safeTitle}";
                            }));
                        }
                        // ==================================================================
                        // JAVÍTÁS (2025.11.04): A hibát már nem "elnyeljük" Debug.WriteLine-nal,
                        // hanem felugró ablakkal jelezzük a felhasználónak!
                        // ==================================================================
                        catch (Exception ex)
                        {
                            // A háttérszálról (Task.Run) az UI szálra (Invoke) kell váltanunk a MessageBox-hoz
                            this.Invoke((Action)(() => {
                                MessageBox.Show($"Error processing '{video.Title}':\n\n{ex.Message}", "Download Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }));

                            // A progress bar-t attól még frissítjük, hogy jelezzük,
                            // a program tovább haladt a következő videóra.
                            int currentCount = Interlocked.Increment(ref completedCount);
                            this.Invoke((Action)(() => {
                                progressBar.Value = currentCount;
                            }));
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }

                // 7. Lépés: Várakozás az ÖSSZES feladat befejezésére
                await Task.WhenAll(tasks);

                // ANGOLRA CSERÉLVE
                statusLabel.Text = "Download complete!";
                MessageBox.Show("Done!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                statusLabel.Text = "An error occurred."; // ANGOLRA CSERÉLVE
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                // 8. Lépés: UI visszaengedélyezése
                UnlockUI();
            }
        }

        // ==================================================================
        // JAVÍTVA: Ez lett a fő "trigger" metódus.
        // Ide került át az URL ellenőrzés logikája.
        // ==================================================================
        private async void formatComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            string selectedFormat = formatComboBox.SelectedItem?.ToString();

            // ==================================================================
            // JAVÍTÁS: "Biztonsági őr"
            // Ha a kiválasztott elem nem "MP3" vagy "MP4" (pl. még a "Select..." szöveg),
            // akkor ne csináljon semmit.
            // ==================================================================
            if (selectedFormat != "MP3" && selectedFormat != "MP4")
            {
                return; // Nem indítjuk el a szkennelést
            }

            // JAVÍTÁS: A felület lezárása a szkennelés és frissítés alatt
            LockUI("Checking URL and fetching qualities..."); // ANGOLRA CSERÉLVE

            try
            {
                // 1. LÉPÉS: URL Szkennelés (áthelyezve ide)
                string url = urlTextBox.Text;
                var videoId = YoutubeExplode.Videos.VideoId.TryParse(url);
                var playlistId = YoutubeExplode.Playlists.PlaylistId.TryParse(url);

                // Alaphelyzet: elrejtünk mindent és töröljük a cache-t
                qualityLabel.Visible = false;
                qualityComboBox.Visible = false;
                qualityComboBox.Items.Clear();
                thumbnailPreviewBox.Visible = false; // ÚJ: Thumbnail elrejtése szkenneléskor
                thumbnailPreviewBox.Image = null; // ÚJ: Kép törlése
                _currentManifest = null;
                _isPlaylist = false;

                if (playlistId != null)
                {
                    _isPlaylist = true;
                    qualityLabel.Text = "Quality:";
                }
                else if (videoId != null)
                {
                    _isPlaylist = false;
                    qualityLabel.Text = "Quality:";
                    try
                    {
                        // ÚJ: Először a videó adatait kérjük le (a thumbnailhez)
                        var video = await _youtubeClient.Videos.GetAsync(videoId.Value);

                        // ==================================================================
                        // JAVÍTÁS: A 'GetWithHighestResolution()' metódus nem létezik.
                        // Helyette LINQ-val rendezzük a listát felbontás (Area) szerint, és kiválasztjuk a legelsőt.
                        // ==================================================================
                        var thumbnailUrl = video.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url; // Ez tölti be a képet

                        thumbnailPreviewBox.ImageLocation = thumbnailUrl; // Ez tölti be a képet

                        // Ezután kérjük le a streameket (mint eddig)
                        _currentManifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId.Value);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Manifest error: {ex.Message}"); // ANGOLRA CSERÉLVE

                        // ==================================================================
                        // JAVÍTÁS: Ne haljunk el csendben! Tájékoztassuk a felhasználót.
                        // ==================================================================
                        MessageBox.Show($"Failed to fetch video details: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return; // Hiba esetén kilépünk (a finally feloldja az UI-t)
                    }
                }
                else
                {
                    // ==================================================================
                    // JAVÍTÁS: Ne haljunk el csendben, ha érvénytelen az URL
                    // (de csak akkor, ha nem üres a mező)
                    // ==================================================================
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        MessageBox.Show("The URL is not a valid YouTube video or playlist link.", "Invalid URL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    return; // Érvénytelen URL (a finally feloldja az UI-t)
                }

                // 2. LÉPÉS: Minőséglista betöltése (a régi logika)
                // string selectedFormat = formatComboBox.SelectedItem?.ToString(); // Ez már megvan fent

                if (_isPlaylist)
                {
                    // Lejátszási lista: Mindig a "High, Medium, Low"
                    PopulateFallbackQualities();
                }
                else if (_currentManifest != null)
                {
                    // Egyetlen videó: Dinamikusan töltjük
                    if (selectedFormat == "MP3")
                    {
                        var audioStreams = _currentManifest.GetAudioOnlyStreams()
                            .OrderByDescending(s => s.Bitrate)
                            .ToList();

                        if (audioStreams.Count > 0)
                        {
                            foreach (var s in audioStreams)
                            {
                                // JAVÍTÁS: Kerekítés a tizedesjegyek elkerülésére
                                string bitrateLabel = $"~{Math.Round(s.Bitrate.KiloBitsPerSecond)} kbps";
                                if (!qualityComboBox.Items.Contains(bitrateLabel))
                                {
                                    qualityComboBox.Items.Add(bitrateLabel);
                                }
                            }
                        }
                    }
                    else if (selectedFormat == "MP4")
                    {
                        // ==================================================================
                        // JAVÍTÁS (360p Hiba): Mostantól a GetVideoOnlyStreams() listát
                        // használjuk, hogy a 720p feletti minőségek is megjelenjenek.
                        // ==================================================================
                        var videoStreams = _currentManifest.GetVideoOnlyStreams() // <-- EZ VÁLTOZOTT
                            .Where(s => s.Container.Name == "mp4" || s.Container.Name == "webm")
                            .OrderByDescending(s => s.VideoQuality)
                            .ToList();

                        if (videoStreams.Count > 0)
                        {
                            foreach (var s in videoStreams)
                            {
                                string qualityLabel = s.VideoQuality.Label; // pl. "1080p"
                                if (!qualityComboBox.Items.Contains(qualityLabel))
                                {
                                    qualityComboBox.Items.Add(qualityLabel);
                                }
                            }
                        }
                    }

                    if (qualityComboBox.Items.Count == 0)
                    {
                        PopulateFallbackQualities();
                    }
                }
                else
                {
                    PopulateFallbackQualities();
                }

                // 3. LÉPÉS: Felület láthatóvá tétele
                qualityLabel.Visible = true;
                qualityComboBox.Visible = true;
                if (!_isPlaylist) // ÚJ: Csak akkor mutatjuk a képet, ha egy videó van
                {
                    thumbnailPreviewBox.Visible = true;
                }
                if (qualityComboBox.Items.Count > 0)
                    qualityComboBox.SelectedIndex = 0; // Legjobb kiválasztása
            }
            finally
            {
                // JAVÍTÁS: Mindig feloldjuk az UI-t
                UnlockUI();
            }
        }


        private string SanitizeFileName(string fileName)
        {
            string invalidChars = new string(Path.GetInvalidFileNameChars());
            string pattern = $"[{Regex.Escape(invalidChars)}]";
            return Regex.Replace(fileName, pattern, "_");
        }

        // ==================================================================
        // JAVÍTÁS (360p Hiba): Ez a metódus teljesen át lett írva.
        // Mostantól letölti a videót és a hangot külön, majd FFmpeg-gel
        // összerakja (muxing).
        // ==================================================================
        private async Task DownloadAsMp4Async(YoutubeClient youtube, YoutubeExplode.Videos.IVideo video, string downloadFolder, string safeTitle, string selectedQuality)
        {
            var manifest = _currentManifest ?? await youtube.Videos.Streams.GetManifestAsync(video.Id);

            // 1. Legjobb AUDIO stream kiválasztása (erre mindig szükség van)
            var audioStreamInfo = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
            if (audioStreamInfo == null)
            {
                throw new Exception($"No audio stream found for this video: {safeTitle}");
            }

            // 2. A kért VIDEO stream kiválasztása (már a VideoOnly streamekből)
            var videoStreams = manifest.GetVideoOnlyStreams()
                .OrderByDescending(s => s.VideoQuality)
                .ToList();

            if (videoStreams.Count == 0)
            {
                throw new Exception($"No video-only stream found for this video: {safeTitle}");
            }

            // JAVÍTÁS: Átírva IVideoStreamInfo-ra, hogy a .VideoQuality elérhető legyen
            IVideoStreamInfo videoStreamInfo;
            switch (selectedQuality)
            {
                case "High":
                    videoStreamInfo = videoStreams.First();
                    break;
                case "Medium":
                    videoStreamInfo = videoStreams[videoStreams.Count / 2];
                    break;
                case "Low":
                    videoStreamInfo = videoStreams.Last();
                    break;
                default:
                    videoStreamInfo = videoStreams.FirstOrDefault(s => s.VideoQuality.Label == selectedQuality);
                    if (videoStreamInfo == null)
                    {
                        videoStreamInfo = videoStreams.First();
                        Debug.WriteLine($"Warning: Requested MP4 quality ({selectedQuality}) not found. Downloading 'High'.");
                    }
                    break;
            }

            // 3. Ideiglenes fájlok és kimeneti fájl meghatározása
            string tempVideoFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.{videoStreamInfo.Container.Name}");
            string tempAudioFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.{audioStreamInfo.Container.Name}");
            // A kimenet mindig .mp4 lesz, függetlenül a letöltött konténertől
            string outputFile = Path.Combine(downloadFolder, $"{safeTitle} [{videoStreamInfo.VideoQuality.Label}].mp4");

            try
            {
                // 4. Letöltés párhuzamosan
                // JAVÍTÁS: A .DownloadAsync() ValueTask-ot ad vissza.
                // A Task.WhenAll()-nak Task-ra van szüksége, ezért .AsTask()-ot használunk.
                var downloadVideoTask = youtube.Videos.Streams.DownloadAsync(videoStreamInfo, tempVideoFile).AsTask();
                var downloadAudioTask = youtube.Videos.Streams.DownloadAsync(audioStreamInfo, tempAudioFile).AsTask();
                await Task.WhenAll(downloadVideoTask, downloadAudioTask);

                // 5. Összerakás (Muxing) FFmpeg-gel
                await MuxVideoAndAudioAsync(tempVideoFile, tempAudioFile, outputFile);
            }
            finally
            {
                // 6. Ideiglenes fájlok törlése (akkor is, ha hiba volt)
                if (File.Exists(tempVideoFile)) File.Delete(tempVideoFile);
                if (File.Exists(tempAudioFile)) File.Delete(tempAudioFile);
            }
        }

        private async Task DownloadAsMp3Async(YoutubeClient youtube, YoutubeExplode.Videos.IVideo video, string downloadFolder, string safeTitle, string selectedQuality)
        {
            var manifest = _currentManifest ?? await youtube.Videos.Streams.GetManifestAsync(video.Id);

            var audioStreams = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .ToList();

            if (audioStreams.Count == 0)
            {
                // ANGOLRA CSERÉLVE
                throw new Exception($"No downloadable audio stream found for this video: {safeTitle}");
            }

            // JAVÍTÁS: Átírva IAudioStreamInfo-ra
            IAudioStreamInfo streamInfo;
            switch (selectedQuality)
            {
                case "High": // ANGOLRA CSERÉLVE
                    streamInfo = audioStreams.First();
                    break;
                case "Medium": // ANGOLRA CSERÉLVE
                    streamInfo = audioStreams[audioStreams.Count / 2];
                    break;
                case "Low": // ANGOLRA CSERÉLVE
                    streamInfo = audioStreams.Last();
                    break;
                default:
                    streamInfo = audioStreams.FirstOrDefault(s => $"~{Math.Round(s.Bitrate.KiloBitsPerSecond)} kbps" == selectedQuality);
                    if (streamInfo == null)
                    {
                        streamInfo = audioStreams.First();
                        // ANGOLRA CSERÉLVE
                        Debug.WriteLine($"Warning: Requested audio quality ({selectedQuality}) not found. Downloading 'High'.");
                    }
                    break;
            }

            string tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.{streamInfo.Container.Name}");
            await youtube.Videos.Streams.DownloadAsync(streamInfo, tempFile);

            // JAVÍTÁS: A fájlnévbe is a kerekített érték kerül
            string outputFile = Path.Combine(downloadFolder, $"{safeTitle} [~{Math.Round(streamInfo.Bitrate.KiloBitsPerSecond)}kbps].mp3");

            await ConvertToMp3Async(tempFile, outputFile);
            File.Delete(tempFile);
        }

        private Task ConvertToMp3Async(string inputFile, string outputFile)
        {
            // JAVÍTÁS (2025.11.04): Az új, okosabb GetFfmpegPath() metódust használjuk
            string ffmpegPath = GetFfmpegPath();
            if (!File.Exists(ffmpegPath))
            {
                // JAVÍTÁS: Pontosított hibaüzenet
                throw new FileNotFoundException(
                    "'ffmpeg.exe' was not found in the application directory or '/bin' sub-directory!" + // ANGOLRA CSERÉLVE
                    "It is required for MP3 conversion. Please download it and copy it to: " +
                    AppDomain.CurrentDomain.BaseDirectory);
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{inputFile}\" -q:a 0 \"{outputFile}\" -y",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var tcs = new TaskCompletionSource<bool>();
            var process = new Process
            {
                StartInfo = processStartInfo,
                EnableRaisingEvents = true
            };

            process.Exited += (sender, args) =>
            {
                if (process.ExitCode == 0)
                {
                    tcs.SetResult(true);
                }
                else
                {
                    string error = process.StandardError.ReadToEnd();
                    tcs.SetException(new Exception($"FFmpeg error: {error}")); // ANGOLRA CSERÉLVE
                }
                process.Dispose();
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }

            return tcs.Task;
        }

        // ==================================================================
        // ÚJ SEGÉDMETÓDUS (360p Hiba javításához)
        // Ez rakja össze a külön letöltött videót és hangot FFmpeg-gel.
        // ==================================================================
        private Task MuxVideoAndAudioAsync(string videoFile, string audioFile, string outputFile)
        {
            // JAVÍTÁS (2025.11.04): Az új, okosabb GetFfmpegPath() metódust használjuk
            string ffmpegPath = GetFfmpegPath();
            if (!File.Exists(ffmpegPath))
            {
                // JAVÍTÁS: Pontosított hibaüzenet
                throw new FileNotFoundException(
                    "'ffmpeg.exe' was not found in the application directory or '/bin' sub-directory!" + // ANGOLRA CSERÉLVE
                    "It is required for high-quality MP4 merging. Please download it and copy it to: " +
                    AppDomain.CurrentDomain.BaseDirectory);
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                // Argumentumok: 
                // -i [videófájl] -i [hangfájl]
                // -c:v copy -c:a copy (NEM kódol újra, csak átmásolja a streameket. EZ GYORS!)
                // [outputfájl] -y (felülírja)
                Arguments = $"-i \"{videoFile}\" -i \"{audioFile}\" -c:v copy -c:a copy \"{outputFile}\" -y",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var tcs = new TaskCompletionSource<bool>();
            var process = new Process
            {
                StartInfo = processStartInfo,
                EnableRaisingEvents = true
            };

            process.Exited += (sender, args) =>
            {
                if (process.ExitCode == 0)
                {
                    tcs.SetResult(true);
                }
                else
                {
                    string error = process.StandardError.ReadToEnd();
                    tcs.SetException(new Exception($"FFmpeg muxing error: {error}"));
                }
                process.Dispose();
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }

            return tcs.Task;
        }

        private void label2_Click(object sender, EventArgs e)
        {

        }
    } // Form1 osztály vége
} // Namespace vége

