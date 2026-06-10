import sqlite3, re, unicodedata

DB = r'W:\Scripts\Chronicle\src\Chronicle.API\chronicle-dev.db'

def normalize(name):
    s = unicodedata.normalize('NFKD', name)
    s = re.sub(r'[^\w\s]', '', s, flags=re.UNICODE)
    s = re.sub(r'\s+', ' ', s).strip().lower()
    return s

db = sqlite3.connect(DB)
cur = db.cursor()

cur.execute("SELECT Id FROM media_types WHERE Name='movies'")
movie_type_ids = [r[0] for r in cur.fetchall()]
placeholders = ','.join('?' * len(movie_type_ids))

cur.execute(
    f'SELECT Id, Name FROM media_items WHERE (Year IS NULL OR Year=0) AND MediaTypeId IN ({placeholders})',
    movie_type_ids
)
rows = cur.fetchall()

pattern = re.compile(r'^(.*)\s+\((\d{4})\)$')
updates = []
for item_id, name in rows:
    m = pattern.match(name)
    if m:
        clean_name = m.group(1).strip()
        year = int(m.group(2))
        norm = normalize(clean_name)
        updates.append((clean_name, year, norm, item_id))

print(f'Found {len(updates)} items to fix')

if updates:
    cur.executemany(
        'UPDATE media_items SET Name=?, Year=?, normalized_name=? WHERE Id=?',
        updates
    )
    item_ids = [u[3] for u in updates]
    placeholders2 = ','.join('?' * len(item_ids))
    cur.execute(
        f"UPDATE media_enrichment SET Status=0, RetryCount=0, ErrorMessage=NULL "
        f"WHERE MediaItemId IN ({placeholders2}) AND PluginId='chronicle.plugin.tmdb'",
        item_ids
    )
    reset_count = cur.rowcount
    db.commit()
    print(f'Updated {len(updates)} item names/years')
    print(f'Reset {reset_count} TMDB enrichment rows to Pending')

db.close()
