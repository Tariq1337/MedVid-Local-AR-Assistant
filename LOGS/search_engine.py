"""
MedVidSearch - Standalone Medical Video Search Engine
=====================================================
Searches medical_db.json using fuzzy matching + synonym expansion + LLM reasoning
to find the best instructional video for a user's medical question.

ONLY returns results for videos that actually exist on disk.

Usage:
    from search_engine import MedVidSearch
    engine = MedVidSearch()
    result = engine.search("my neck is really stiff")
"""

import os
import json
import re
from difflib import SequenceMatcher
from openai import OpenAI

# --- PATHS (Windows paths accessed from WSL) ---
BASE_DIR = "/mnt/c/Users/tbahaaal/Documents/LOGS/MedVid_DATA"
MEDICAL_DB_PATH = os.path.join(BASE_DIR, "medical_db.json")
VIDEOS_DIR = os.path.join(BASE_DIR, "videos")

# --- LLM CONFIG ---
LLM_URL = "http://localhost:22002/v1"
LLM_MODEL = "Qwen/Qwen3-VL-8B-Instruct"

# --- SYNONYM / MEDICAL TERM EXPANSION ---
# When user says X, also search for Y (bidirectional)
SYNONYM_GROUPS = [
    # Symptoms <-> Medical terms
    {"dizzy", "dizziness", "vertigo", "lightheaded", "spinning", "balance", "epley"},
    {"stiff", "stiffness", "tight", "tightness", "sore", "soreness"},
    {"numb", "numbness", "tingling", "pins"},
    {"swollen", "swelling", "inflamed", "inflammation", "puffy"},
    {"pain", "painful", "hurts", "ache", "aching", "sore"},
    {"bleeding", "blood", "hemorrhage", "bleed"},
    {"broken", "fracture", "fractured", "break"},
    {"sprain", "sprained", "twisted", "rolled"},
    {"burn", "burned", "burnt", "scald", "scalded"},
    {"cut", "laceration", "wound", "gash", "slice"},
    {"rash", "itchy", "irritation", "dermatitis", "eczema"},
    {"headache", "migraine", "head pain"},
    {"fever", "temperature", "feverish"},
    {"cough", "coughing", "bronchitis"},
    {"breathing", "breathless", "shortness", "asthma", "wheezing", "respiratory"},
    {"choking", "choke", "heimlich", "obstruction", "airway"},
    {"faint", "fainting", "unconscious", "passed", "syncope", "unresponsive"},
    {"seizure", "convulsion", "epilepsy", "fitting"},
    {"vomit", "vomiting", "nausea", "nauseous", "throwing"},
    {"diarrhea", "diarrhoea", "loose", "stomach"},

    # Body parts
    {"ankle", "foot", "feet"},
    {"wrist", "hand", "forearm"},
    {"knee", "kneecap", "patella", "squat", "squatting"},
    {"shoulder", "rotator", "deltoid", "impingement"},
    {"neck", "cervical", "throat"},
    {"back", "spine", "spinal", "lumbar", "thoracic"},
    {"hip", "pelvis", "groin"},
    {"calf", "calves", "gastrocnemius", "soleus"},
    {"hamstring", "hamstrings", "thigh"},
    {"elbow", "tennis", "golfer"},
    {"finger", "thumb", "digit", "knuckle"},
    {"toe", "toes", "big toe"},
    {"chest", "rib", "ribs", "sternum"},
    {"eye", "eyes", "vision", "sight"},
    {"ear", "ears", "hearing"},

    # Treatments
    {"stretch", "stretching", "flexibility", "loosen"},
    {"exercise", "exercises", "workout", "strengthen", "strengthening"},
    {"massage", "rub", "release", "foam roll"},
    {"bandage", "wrap", "dressing", "tape", "taping"},
    {"splint", "brace", "immobilize", "support"},
    {"ice", "cold", "compress", "cryotherapy"},
    {"heat", "warm", "heating pad", "hot"},
    {"cpr", "resuscitation", "chest compressions"},
    {"aed", "defibrillator", "shock"},
    {"tourniquet", "truncate", "bleeding control"},
    {"injection", "inject", "syringe", "needle"},
    {"inhaler", "puffer", "nebulizer", "bronchodilator"},

    # Actions
    {"remove", "take out", "extract", "pull out"},
    {"treat", "treatment", "remedy", "cure", "fix", "heal", "relieve", "relief"},
    {"check", "test", "examine", "assess", "evaluate", "diagnose"},
    {"prevent", "prevention", "avoid", "protect"},
]


class MedVidSearch:
    def __init__(self):
        print("📂 Loading medical_db.json...")
        with open(MEDICAL_DB_PATH, "r", encoding="utf-8") as f:
            raw_db = json.load(f)

        # Scan which videos actually exist on disk
        print(f"📁 Scanning videos folder: {VIDEOS_DIR}")
        if os.path.exists(VIDEOS_DIR):
            existing_videos = {f.replace(".mp4", "") for f in os.listdir(VIDEOS_DIR) if f.endswith(".mp4")}
        else:
            existing_videos = set()
            print("   ⚠️ Videos folder not found!")

        # FILTER: Only keep questions whose video exists
        self.medical_db = {}
        skipped = 0
        for question, video_data in raw_db.items():
            video_id = list(video_data.keys())[0]
            if video_id in existing_videos:
                self.medical_db[question] = video_data
            else:
                skipped += 1

        # Pre-build a flat list for fast searching
        self.questions = list(self.medical_db.keys())
        self.questions_lower = [q.lower() for q in self.questions]

        # Build synonym lookup: word -> set of expanded words
        self.synonym_map = {}
        for group in SYNONYM_GROUPS:
            for word in group:
                if word not in self.synonym_map:
                    self.synonym_map[word] = set()
                self.synonym_map[word].update(group - {word})

        # Build keyword index: word -> set of question indices
        self.keyword_index = {}
        for idx, q in enumerate(self.questions_lower):
            words = set(re.findall(r'[a-z]+', q))
            for word in words:
                if len(word) > 2:
                    if word not in self.keyword_index:
                        self.keyword_index[word] = set()
                    self.keyword_index[word].add(idx)

        self.client = OpenAI(base_url=LLM_URL, api_key="EMPTY")
        print(f"✅ Loaded {len(self.questions)} questions (skipped {skipped} with missing videos)")
        print(f"✅ Available videos: {len(existing_videos)}")
        print(f"✅ Keyword index: {len(self.keyword_index)} unique terms")
        print(f"✅ Synonym groups: {len(SYNONYM_GROUPS)} ({len(self.synonym_map)} terms mapped)")

    # ------------------------------------------------------------------
    #  SYNONYM EXPANSION
    # ------------------------------------------------------------------
    def _expand_query_words(self, words):
        """
        Takes a set of words from the user query and expands them
        using synonym groups. Returns the expanded set.
        Example: {"dizzy", "lying"} -> {"dizzy", "dizziness", "vertigo", "lightheaded", "spinning", "balance", "lying"}
        """
        expanded = set(words)
        for word in words:
            if word in self.synonym_map:
                expanded.update(self.synonym_map[word])
        return expanded

    # ------------------------------------------------------------------
    #  STEP 1: Fast candidate retrieval (no LLM needed)
    # ------------------------------------------------------------------
    def _get_candidates(self, query, top_n=10):
        """
        Three-pass scoring:
          Pass 1 - Expand query words with synonyms
          Pass 2 - Keyword overlap (fast, narrows to ~50-100)
          Pass 3 - SequenceMatcher similarity on the survivors
        Returns top_n candidates sorted by combined score.
        """
        query_lower = query.lower().strip()
        query_words = set(re.findall(r'[a-z]+', query_lower))
        query_words = {w for w in query_words if len(w) > 2}

        # Filter out stopwords that cause noise in matching
        STOPWORDS = {
            "the", "and", "for", "that", "this", "with", "from", "your", "you",
            "how", "what", "when", "where", "why", "who", "which", "can", "could",
            "should", "would", "will", "does", "did", "has", "have", "had", "are",
            "was", "were", "been", "being", "not", "but", "they", "them", "then",
            "than", "its", "his", "her", "our", "any", "all", "each", "every",
            "some", "into", "also", "just", "very", "really", "too", "much",
            "feel", "feeling", "like", "want", "need", "get", "got", "make",
            "know", "think", "after", "before", "about", "down", "there",
        }
        query_words = query_words - STOPWORDS

        # Pass 1: Synonym expansion
        original_words = set(query_words)
        expanded_words = self._expand_query_words(query_words)
        new_synonyms = expanded_words - original_words
        if new_synonyms:
            print(f"   🔗 Synonyms added: {', '.join(sorted(new_synonyms))}")

        # Pass 2: Keyword scoring (using expanded words)
        candidate_scores = {}
        for word in expanded_words:
            if word in self.keyword_index:
                # Medical synonyms get HIGH weight — they bridge vague queries to real answers
                if word in new_synonyms:
                    weight = 1.5
                elif word in original_words:
                    weight = 1.0
                else:
                    weight = 0.5
                for idx in self.keyword_index[word]:
                    candidate_scores[idx] = candidate_scores.get(idx, 0) + weight

        # If no keyword hits, fall back to full scan
        if not candidate_scores:
            candidates_to_check = range(len(self.questions))
        else:
            # Take top 100 by keyword overlap (wider net)
            sorted_by_keywords = sorted(candidate_scores.items(), key=lambda x: -x[1])
            candidates_to_check = [idx for idx, _ in sorted_by_keywords[:100]]

        # Pass 3: SequenceMatcher on BOTH original + expanded query
        expanded_query = query_lower + " " + " ".join(new_synonyms)

        scored = []
        for idx in candidates_to_check:
            db_q = self.questions_lower[idx]
            # Keyword score normalized by meaningful word count
            meaningful_count = max(len(original_words | (new_synonyms & set(self.keyword_index.keys()))), 1)
            kw_score = candidate_scores.get(idx, 0) / meaningful_count
            # Fuzzy match against both original and expanded, take the better one
            fuzzy_original = SequenceMatcher(None, query_lower, db_q).ratio()
            fuzzy_expanded = SequenceMatcher(None, expanded_query, db_q).ratio()
            fuzzy_score = max(fuzzy_original, fuzzy_expanded)
            combined = (kw_score * 0.45) + (fuzzy_score * 0.55)
            scored.append((idx, combined, kw_score, fuzzy_score))

        scored.sort(key=lambda x: -x[1])
        return scored[:top_n]

    # ------------------------------------------------------------------
    #  STEP 2: LLM picks the best match from candidates
    # ------------------------------------------------------------------
    def _llm_select(self, user_query, candidates):
        """
        Sends the user query + top candidates to Qwen.
        The LLM picks the single best match or says 'NONE'.
        """
        options = ""
        for i, (idx, score, _, _) in enumerate(candidates):
            options += f"  {i+1}. {self.questions[idx]}\n"

        prompt = f"""You are a medical video search assistant. A user has a health concern and I found potential matching videos from a medical instructional database.

IMPORTANT: Users may describe SYMPTOMS (e.g. "I feel dizzy") rather than asking for specific procedures. Match symptoms to relevant treatments. For example:
- "I feel dizzy when I lie down" matches videos about treating dizziness or vertigo
- "my knee hurts" matches videos about knee pain relief or knee exercises
- "I can't breathe well" matches videos about breathing exercises or asthma

USER QUESTION: "{user_query}"

CANDIDATE MATCHES:
{options}
TASK: Pick the ONE best match that would help this user. If NONE of them are medically relevant to the user's concern, say NONE.

Reply with ONLY the number (e.g. "3") or "NONE". No explanation. No thinking tags."""

        try:
            response = self.client.chat.completions.create(
                model=LLM_MODEL,
                messages=[
                    {"role": "system", "content": "You are a medical video matcher. Match user symptoms to treatment videos. Reply with only a number or NONE. No thinking tags."},
                    {"role": "user", "content": prompt}
                ],
                max_tokens=20,
                temperature=0.1
            )
            answer = response.choices[0].message.content.strip()
            # Clean up: extract just the number
            answer = re.sub(r'<[^>]+>', '', answer).strip()
            numbers = re.findall(r'\d+', answer)
            if numbers:
                pick = int(numbers[0])
                if 1 <= pick <= len(candidates):
                    return pick - 1
            if "none" in answer.lower():
                return None
            return None
        except Exception as e:
            print(f"   ⚠️ LLM error: {e}")
            return 0

    # ------------------------------------------------------------------
    #  STEP 3: Build the result
    # ------------------------------------------------------------------
    def _build_result(self, question_key):
        """Given a medical_db question key, return structured result."""
        video_data = self.medical_db[question_key]
        video_id = list(video_data.keys())[0]
        info = video_data[video_id]

        video_filename = f"{video_id}.mp4"
        video_path = os.path.join(VIDEOS_DIR, video_filename)

        return {
            "matched_question": question_key,
            "video_id": video_id,
            "video_file": video_filename,
            "video_path": video_path,
            "start_second": info["bounds"][0],
            "end_second": info["bounds"][1],
            "duration": info["bounds"][1] - info["bounds"][0],
            "steps": info["steps"]
        }

    # ------------------------------------------------------------------
    #  PUBLIC: Main search function
    # ------------------------------------------------------------------
    def search(self, user_query, use_llm=True):
        """
        Main search pipeline:
          1. Expand query with synonyms
          2. Fuzzy match to get top 10 candidates
          3. LLM picks the best one (or NONE)
          4. Return structured result

        Args:
            user_query: Natural language medical question
            use_llm: If False, skip LLM and return top fuzzy match

        Returns:
            dict with video info, or None if no match
        """
        print(f"\n🔍 Searching: \"{user_query}\"")

        # Step 1: Get candidates
        candidates = self._get_candidates(user_query, top_n=10)

        if not candidates:
            print("   ❌ No candidates found")
            return None

        # Show top 5 for debugging
        print("   📋 Top candidates:")
        for i, (idx, combined, kw, fz) in enumerate(candidates[:5]):
            print(f"      {i+1}. [{combined:.3f}] {self.questions[idx]}")

        # Step 2: LLM selection
        if use_llm:
            print("   🧠 Asking LLM to pick best match...")
            pick = self._llm_select(user_query, candidates)

            if pick is None:
                print("   ❌ LLM said: No relevant match")
                return None

            chosen_idx = candidates[pick][0]
            print(f"   ✅ LLM picked #{pick+1}: {self.questions[chosen_idx]}")
        else:
            chosen_idx = candidates[0][0]
            print(f"   ✅ Top match: {self.questions[chosen_idx]}")

        # Step 3: Build result
        result = self._build_result(self.questions[chosen_idx])

        print(f"   🎬 Video: {result['video_file']}")
        print(f"   ⏱️  Play: {result['start_second']}s → {result['end_second']}s ({result['duration']}s)")

        return result


# --- RUN DIRECTLY ---
if __name__ == "__main__":
    engine = MedVidSearch()

    print("\n" + "="*60)
    print("  MedVid Search Engine - Interactive Mode")
    print("  Type a medical question, or 'quit' to exit")
    print("="*60)

    while True:
        print()
        query = input("❓ Your question: ").strip()
        if query.lower() in ["quit", "exit", "q"]:
            print("👋 Goodbye!")
            break
        if not query:
            continue

        result = engine.search(query, use_llm=True)

        if result:
            print(f"\n   ╔══════════════════════════════════════════╗")
            print(f"   ║  RESULT                                  ║")
            print(f"   ╠══════════════════════════════════════════╣")
            print(f"   ║  Match: {result['matched_question'][:40]}")
            print(f"   ║  Video: {result['video_file']}")
            print(f"   ║  Play:  {result['start_second']}s → {result['end_second']}s")
            print(f"   ╚══════════════════════════════════════════╝")
        else:
            print("\n   ❌ No matching medical video found.")
            print("   💬 The AI will answer normally (general mode).")