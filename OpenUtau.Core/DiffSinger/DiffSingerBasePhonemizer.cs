using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using OpenUtau.Api;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.DiffSinger
{
    public abstract class DiffSingerBasePhonemizer : MachineLearningPhonemizer
    {
        USinger singer;
        DsConfig dsConfig;
        string rootPath;
        float frameMs;
        InferenceSession linguisticModel;
        InferenceSession durationModel;
        IG2p g2p;
        List<string> phonemes;
        DiffSingerSpeakerEmbedManager speakerEmbedManager;

        string defaultPause = "SP";

        public override void SetSinger(USinger singer) {
            this.singer = singer;
            if (File.Exists(Path.Join(singer.Location, "dsdur", "dsconfig.yaml"))) {
                rootPath = Path.Combine(singer.Location, "dsdur");
            } else {
                rootPath = singer.Location;
            }
            //Load Config
            var configPath = Path.Join(rootPath, "dsconfig.yaml");
            try {
                var configTxt = File.ReadAllText(configPath);
                dsConfig = Yaml.DefaultDeserializer.Deserialize<DsConfig>(configTxt);
            } catch(Exception e) {
                Log.Error(e, $"failed to load dsconfig from {configPath}");
                return;
            }
            this.frameMs = dsConfig.frameMs();
            //Load g2p
            g2p = LoadG2p(rootPath);
            //Load phonemes list
            string phonemesPath = Path.Combine(rootPath, dsConfig.phonemes);
            phonemes = File.ReadLines(phonemesPath,singer.TextFileEncoding).ToList();
            //Load models
            var linguisticModelPath = Path.Join(rootPath, dsConfig.linguistic);
            try {
                linguisticModel = new InferenceSession(linguisticModelPath);
            } catch (Exception e) {
                Log.Error(e, $"failed to load linguistic model from {linguisticModelPath}");
                return;
            }
            var durationModelPath = Path.Join(rootPath, dsConfig.dur);
            try {
                durationModel = new InferenceSession(durationModelPath);
            } catch (Exception e) {
                Log.Error(e, $"failed to load duration model from {durationModelPath}");
                return;
            }
        }

        protected virtual IG2p LoadG2p(string rootPath) {
            var g2ps = new List<IG2p>();
            // Load dictionary from singer folder.
            string file = Path.Combine(rootPath, "dsdict.yaml");
            if (File.Exists(file)) {
                try {
                    g2ps.Add(G2pDictionary.NewBuilder().Load(File.ReadAllText(file)).Build());
                } catch (Exception e) {
                    Log.Error(e, $"Failed to load {file}");
                }
            }
            return new G2pFallbacks(g2ps.ToArray());
        }

        string[] GetSymbols(Note note) {
            //priority:
            //1. phonetic hint
            //2. query from g2p dictionary
            //3. treat lyric as phonetic hint, including single phoneme
            //4. default pause
            if (!string.IsNullOrEmpty(note.phoneticHint)) {
                // Split space-separated symbols into an array.
                return note.phoneticHint.Split()
                    .Where(s => g2p.IsValidSymbol(s)) // skip the invalid symbols.
                    .ToArray();
            }
            // User has not provided hint, query g2p dictionary.
            var g2presult = g2p.Query(note.lyric)
                ?? g2p.Query(note.lyric.ToLowerInvariant());
            if(g2presult != null) {
                return g2presult;
            }
            //not founded in g2p dictionary, treat lyric as phonetic hint
            var lyricSplited = note.lyric.Split()
                    .Where(s => g2p.IsValidSymbol(s)) // skip the invalid symbols.
                    .ToArray();
            if (lyricSplited.Length > 0) {
                return lyricSplited;
            }
            return new string[] { defaultPause };
        }

        string GetSpeakerAtIndex(Note note, int index){
            var attr = note.phonemeAttributes?.FirstOrDefault(attr => attr.index == index) ?? default;
            var speaker = singer.Subbanks
                .Where(subbank => subbank.Color == attr.voiceColor && subbank.toneSet.Contains(note.tone))
                .FirstOrDefault();
            if(speaker is null) {
                return "";
            }
            return speaker.Suffix;
        }

        dsPhoneme[] GetDsPhonemes(Note note){
            return GetSymbols(note)
                .Select((symbol, index) => new dsPhoneme(symbol, GetSpeakerAtIndex(note, index)))
                .ToArray();            
        }

        protected bool IsSyllableVowelExtensionNote(Note note) {
            return note.lyric.StartsWith("+~") || note.lyric.StartsWith("+*");
        }

        List<phonemesPerNote> ProcessWord(Note[] notes){
            var wordPhonemes = new List<phonemesPerNote>{
                new phonemesPerNote(-1, notes[0].tone)
            };
            var dsPhonemes = GetDsPhonemes(notes[0]);
            var isVowel = dsPhonemes.Select(s => g2p.IsVowel(s.Symbol)).ToArray();
            var nonExtensionNotes = notes.Where(n=>!IsSyllableVowelExtensionNote(n)).ToArray();
            //distribute phonemes to notes
            var noteIndex = 0;
            for (int i = 0; i < dsPhonemes.Length; i++) {
                if (isVowel[i] && noteIndex < nonExtensionNotes.Length) {
                    var note = nonExtensionNotes[noteIndex];
                    wordPhonemes.Add(new phonemesPerNote(note.position, note.tone));
                    noteIndex++;
                }
                wordPhonemes[^1].Phonemes.Add(dsPhonemes[i]);
            }
            return wordPhonemes;
        }

        int framesBetweenTickPos(double tickPos1, double tickPos2) {
            return (int)(timeAxis.TickPosToMsPos(tickPos2)/frameMs) 
                - (int)(timeAxis.TickPosToMsPos(tickPos1)/frameMs);
        }

        public static IEnumerable<double> CumulativeSum(IEnumerable<double> sequence, double start = 0) {
            double sum = start;
            foreach (var item in sequence) {
                sum += item;
                yield return sum;
            }
        }

        public static IEnumerable<int> CumulativeSum(IEnumerable<int> sequence, int start = 0) {
            int sum = start;
            foreach (var item in sequence) {
                sum += item;
                yield return sum;
            }
        }

        public List<double> stretch(IList<double> source, double ratio, double endPos) {
            //source：音素时长序列，单位ms
            //ratio：缩放比例
            //endPos：目标终点时刻，单位ms
            //输出：缩放后的音素位置，单位ms
            double startPos = endPos - source.Sum() * ratio;
            var result = CumulativeSum(source.Select(x => x * ratio).Prepend(0), startPos).ToList();
            result.RemoveAt(result.Count - 1);
            return result;
        }
        
        public DiffSingerSpeakerEmbedManager getSpeakerEmbedManager(){
            if(speakerEmbedManager is null) {
                speakerEmbedManager = new DiffSingerSpeakerEmbedManager(dsConfig, rootPath);
            }
            return speakerEmbedManager;
        }
        
        protected override void ProcessPart(Note[][] phrase) {
            float padding = 500f;//Padding time for consonants at the beginning of a sentence, ms
            float frameMs = dsConfig.frameMs();
            var startMs = timeAxis.TickPosToMsPos(phrase[0][0].position) - padding;
            var lastNote = phrase[^1][^1];
            var endTick = lastNote.position+lastNote.duration;
            //[(Tick position of note, [phonemes])]
            //The first item of this list is for the consonants before the first note.
            var phrasePhonemes = new List<phonemesPerNote>{
                new phonemesPerNote(-1,phrase[0][0].tone, new List<dsPhoneme>{new dsPhoneme("SP", GetSpeakerAtIndex(phrase[0][0], 0))})
            };
            var notePhIndex = new List<int> { 1 };
            foreach (var word in phrase) {
                var wordPhonemes = ProcessWord(word);
                phrasePhonemes[^1].Phonemes.AddRange(wordPhonemes[0].Phonemes);
                phrasePhonemes.AddRange(wordPhonemes.Skip(1));
                notePhIndex.Add(notePhIndex[^1]+wordPhonemes.SelectMany(n=>n.Phonemes).Count());
            }
            
            phrasePhonemes.Add(new phonemesPerNote(endTick,lastNote.tone));
            phrasePhonemes[0].Position = timeAxis.MsPosToTickPos(
                timeAxis.TickPosToMsPos(phrasePhonemes[1].Position)-padding
                );
            //Linguistic Encoder
            var tokens = phrasePhonemes
                .SelectMany(n => n.Phonemes)
                .Select(p => (Int64)phonemes.IndexOf(p.Symbol))
                .ToArray();
            var word_div = phrasePhonemes.Take(phrasePhonemes.Count-1)
                .Select(n => (Int64)n.Phonemes.Count)
                .ToArray();
            //Pairwise(phrasePhonemes)
            var word_dur = phrasePhonemes
                .Zip(phrasePhonemes.Skip(1), (a, b) => (long)framesBetweenTickPos(a.Position, b.Position))
                .ToArray();
            //Call Diffsinger Linguistic Encoder model
            var linguisticInputs = new List<NamedOnnxValue>();
            linguisticInputs.Add(NamedOnnxValue.CreateFromTensor("tokens",
                new DenseTensor<Int64>(tokens, new int[] { tokens.Length }, false)
                .Reshape(new int[] { 1, tokens.Length })));
            linguisticInputs.Add(NamedOnnxValue.CreateFromTensor("word_div",
                new DenseTensor<Int64>(word_div, new int[] { word_div.Length }, false)
                .Reshape(new int[] { 1, word_div.Length })));
            linguisticInputs.Add(NamedOnnxValue.CreateFromTensor("word_dur",
                new DenseTensor<Int64>(word_dur, new int[] { word_dur.Length }, false)
                .Reshape(new int[] { 1, word_dur.Length })));
            Onnx.VerifyInputNames(linguisticModel, linguisticInputs);
            var linguisticOutputs = linguisticModel.Run(linguisticInputs);
            Tensor<float> encoder_out = linguisticOutputs
                .Where(o => o.Name == "encoder_out")
                .First()
                .AsTensor<float>();
            Tensor<bool> x_masks = linguisticOutputs
                .Where(o => o.Name == "x_masks")
                .First()
                .AsTensor<bool>();
            //Duration Predictor
            var ph_midi = phrasePhonemes
                .SelectMany(n=>Enumerable.Repeat((Int64)n.Tone, n.Phonemes.Count))
                .ToArray();
            //Call Diffsinger Duration Predictor model
            var durationInputs = new List<NamedOnnxValue>();
            durationInputs.Add(NamedOnnxValue.CreateFromTensor("encoder_out", encoder_out));
            durationInputs.Add(NamedOnnxValue.CreateFromTensor("x_masks", x_masks));
            durationInputs.Add(NamedOnnxValue.CreateFromTensor("ph_midi",
                new DenseTensor<Int64>(ph_midi, new int[] { ph_midi.Length }, false)
                .Reshape(new int[] { 1, ph_midi.Length })));
            //Speaker
            if(dsConfig.speakers != null){
                var speakerEmbedManager = getSpeakerEmbedManager();
                var speakersByPhone =  phrasePhonemes
                    .SelectMany(n => n.Phonemes)
                    .Select(p => p.Speaker)
                    .ToArray();
                var spkEmbedTensor = speakerEmbedManager.PhraseSpeakerEmbedByPhone(speakersByPhone);
                durationInputs.Add(NamedOnnxValue.CreateFromTensor("spk_embed", spkEmbedTensor));
            }
            Onnx.VerifyInputNames(durationModel, durationInputs);
            var durationOutputs = durationModel.Run(durationInputs);
            List<double> durationFrames = durationOutputs.First().AsTensor<float>().Select(x=>(double)x).ToList();
            
            //Alignment
            //(the index of the phoneme to be aligned, the Ms position of the phoneme)
            var phAlignPoints = new List<Tuple<int, double>>();
            phAlignPoints = CumulativeSum(phrasePhonemes.Select(n => n.Phonemes.Count).ToList(), 0)
                .Zip(phrasePhonemes.Skip(1), 
                    (a, b) => new Tuple<int, double>(a, timeAxis.TickPosToMsPos(b.Position)))
                .ToList();
            var positions = new List<double>();
            List<double> alignGroup = durationFrames.GetRange(1, phAlignPoints[0].Item1 - 1);
            
            var phs = phrasePhonemes.SelectMany(n => n.Phonemes).ToList();
            //The starting consonant's duration keeps unchanged
            positions.AddRange(stretch(alignGroup, frameMs, phAlignPoints[0].Item2));
            //Stretch the duration of the rest phonemes
            foreach (var pair in phAlignPoints.Zip(phAlignPoints.Skip(1), (a, b) => Tuple.Create(a, b))) {
                var currAlignPoint = pair.Item1;
                var nextAlignPoint = pair.Item2;
                alignGroup = durationFrames.GetRange(currAlignPoint.Item1, nextAlignPoint.Item1 - currAlignPoint.Item1);
                double ratio = (nextAlignPoint.Item2 - currAlignPoint.Item2) / alignGroup.Sum();
                positions.AddRange(stretch(alignGroup, ratio, nextAlignPoint.Item2));
            }

            //Convert the position sequence to tick and fill into the result list
            int index = 1;
            foreach (int groupIndex in Enumerable.Range(0, phrase.Length)) {
                Note[] group = phrase[groupIndex];
                var noteResult = new List<Tuple<string, int>>();
                if (group[0].lyric.StartsWith("+")) {
                    continue;
                }
                double notePos = timeAxis.TickPosToMsPos(group[0].position);//start position of the note, ms
                for (int phIndex = notePhIndex[groupIndex]; phIndex < notePhIndex[groupIndex + 1]; ++phIndex) {
                    if (!String.IsNullOrEmpty(phs[phIndex].Symbol)) {
                        noteResult.Add(Tuple.Create(phs[phIndex].Symbol, timeAxis.TicksBetweenMsPos(
                           notePos, positions[phIndex - 1])));
                    }
                }
                partResult[group[0].position] = noteResult;
            }
        }
    }

    struct dsPhoneme{
        public string Symbol;
        public string Speaker;

        public dsPhoneme(string symbol, string speaker){
            Symbol = symbol;
            Speaker = speaker;
        }
    }

    class phonemesPerNote{
        public int Position;
        public int Tone;
        public List<dsPhoneme> Phonemes;

        public phonemesPerNote(int position, int tone, List<dsPhoneme> phonemes)
        {
            Position = position;
            Tone = tone;
            Phonemes = phonemes;
        }

        public phonemesPerNote(int position, int tone)
        {
            Position = position;
            Tone = tone;
            Phonemes = new List<dsPhoneme>();
        }
    }
}
