import json, os, re, glob, subprocess, sys
root = r"W:\Scripts"
host = os.path.join(root, "Chronicle")
survey = json.load(open(sys.argv[1]))
real = [r for r in survey if r["loc"] >= 150]
catalogue = json.load(open(os.path.join(host, "plugins.json"), encoding="utf8")); catalogue = catalogue if isinstance(catalogue, list) else catalogue.get("plugins", [])
cat_ids = {(e.get("plugin_id") or "").lower() for e in catalogue}
catmd = open(os.path.join(host, "docs", "PLUGIN_CATALOGUE.md"), encoding="utf8", errors="ignore").read().lower() if os.path.exists(os.path.join(host, "docs", "PLUGIN_CATALOGUE.md")) else ""
def rd(p):
    try: return open(p, encoding="utf8", errors="ignore").read()
    except Exception: return ""
def sources(p):
    out = {}
    for f in glob.glob(os.path.join(p, "**", "*.cs"), recursive=True):
        l = f.lower()
        if "\\obj\\" in l or "\\bin\\" in l or "\\tests\\" in l or "\\tools\\" in l: continue
        out[f] = rd(f)
    return out
checks = ["K1","K3","K4","K5","K7","K8","K9","K10","K11","K12","B1","B2","B4","B5","B6","X1","X2","X3","M2","C4","Q9"]
res = {}
for r in real:
    name = r["repo"].replace("Chronicle.Plugin.", "")
    p = os.path.join(root, r["repo"])
    files = sources(p); s = "\n".join(files.values())
    mf = json.loads(rd(os.path.join(p, "manifest.json")) or "{}")
    pid = (mf.get("plugin_id") or "").lower()
    out = {}; why = {}
    def put(k, ok, note=""):
        out[k] = "pass" if ok is True else ("na" if ok is None else "FAIL");
        if note and ok is not True: why[k] = note
    # K1
    vers = re.findall(r"string\s+Version\s*(?:=>|\{\s*get;\s*\}\s*=)\s*([^;]+);", s)
    allasm = bool(vers) and all("Assembly" in v for v in vers)
    put("K1", allasm, f"Version literal(s) {[v.strip() for v in vers]} vs manifest {mf.get('version')}")
    # K3
    tag = subprocess.run(["git","-C",p,"describe","--tags","--abbrev=0"],capture_output=True,text=True).stdout.strip().lstrip("v")
    emb = os.path.join(host,"src","Chronicle.API","plugins",pid,"manifest.json")
    embv = None
    if os.path.exists(emb):
        try: embv = json.loads(rd(emb)).get("version")
        except Exception: embv = "?"
    bad = []
    if not pid.startswith("chronicle.plugin."): bad.append(f"plugin_id '{pid}'")
    if pid not in cat_ids and not any(pid == c or pid.endswith("."+c) for c in cat_ids): bad.append("not in plugins.json")
    if tag != (mf.get("version") or ""): bad.append(f"tag '{tag}' vs manifest '{mf.get('version')}'")
    if r.get("csproj_version") != mf.get("version"): bad.append(f"csproj '{r.get('csproj_version')}' vs manifest")
    if embv is None: bad.append("no embedded copy")
    elif embv != mf.get("version"): bad.append(f"embedded {embv}")
    put("K3", not bad, "; ".join(bad))
    # K4
    put("K4", r["n_tests"] > 0, f"{r['n_tests']} tests")
    # K5
    put("K5", r["license"] and r["ci"], f"license={r['license']} ci={r['ci']}")
    # K7
    keyed = bool(re.search(r"SettingType\.Password", s)) or r["c_Redact"] > 0
    put("K7", None if not keyed else r["c_Redact"] > 0, "holds a secret, no redaction code found")
    # K8
    put("K8", r["c_UserAgent"] > 0, "no User-Agent")
    # K9
    timeout = bool(re.search(r"Timeout\s*=", s))
    shared = bool(re.search(r"static\s+(readonly\s+)?HttpClient|IHttpClientFactory", s)) or r["c_HttpClientNew"] <= 1
    net = r["c_HttpRequestException"] > 0 or "HttpClient" in s
    put("K9", None if not net else (timeout and shared), f"timeout={timeout} sharedClient={shared}")
    # K10
    put("K10", None if not net else (r["c_Retry429"] > 0), "no 429 handling")
    # K11
    empt = len(re.findall(r"catch\s*(\([^)]*\))?\s*\{\s*\}", s))
    put("K11", empt == 0, f"{empt} empty catch")
    # K12
    docs = glob.glob(os.path.join(host, "docs", "plugins", "PLUGIN_*"+name.upper().replace(".","")+"*.md")) + glob.glob(os.path.join(host, "docs", "plugins", "PLUGIN_*"+name.replace(".","")+"*.md"))
    docs = docs or [d for d in glob.glob(os.path.join(host, "docs", "plugins", "*.md")) if name.lower().replace(".","") in os.path.basename(d).lower().replace("_","").replace("-","")]
    incat = name.lower() in catmd or pid in catmd
    put("K12", bool(docs) and incat, f"doc page={bool(docs)} catalogue line={incat}")
    # B
    put("B1", bool(mf.get("iconUrl")), "no iconUrl")
    put("B2", None if not (mf.get("background_tasks") or []) else (bool(mf.get("brandColorLight")) and bool(mf.get("brandColorDark"))), "brand colours missing (the colour tints the Background Tasks card, so only plugins with tasks need it)")
    d = mf.get("description") or ""
    put("B4", len(d) >= 60 and bool(mf.get("author")), f"description {len(d)} chars")
    put("B5", bool(mf.get("fixMatchHint")) if mf.get("supported_media_types") else None, "no fixMatchHint")
    tasks = mf.get("background_tasks") or []
    put("B6", None if not tasks else all(t.get("display_name") and t.get("description") and ("default_cron" in t) for t in tasks), "task text incomplete")
    # X
    put("X1", bool(re.search(r"GetSettingsSchema", s)), "no schema")
    defs = re.findall(r"new\s+SettingDefinition\s*\{(.*?)\}", s, re.S)
    nodesc = [x for x in defs if "Description" not in x]
    put("X2", None if not defs else not nodesc, f"{len(nodesc)} of {len(defs)} settings lack help text")
    secrets = [x for x in defs if re.search(r"Key\s*=\s*\"[^\"]*(key|token|secret|password|jwt)[^\"]*\"", x, re.I) and "Password" not in x and "Notice" not in x]
    put("X3", None if not defs else not secrets, f"{len(secrets)} secret-like setting(s) not Password")
    # M2
    m = re.search(r"GetImageAsync\s*\([^)]*\)\s*(\{|=>)(.{0,500})", s, re.S)
    body = m.group(2) if m else ""
    ok = bool(re.search(r"NotSupported|NotImplemented", body)) or bool(re.search(r"(Host|AllowedHosts|ImageHosts|UriScheme)", body)) or "ImageHosts" in s
    put("M2", ok if m else None, "fetches any address")
    # C4
    put("C4", None if not keyed else r["c_PluginAuthException"] > 0, "no PluginAuthException for rejected credentials")
    # Q9
    types = mf.get("supported_media_types") or []
    put("Q9", None if not types else r["c_AlternateNames"] > 0, "no AlternateNames")
    res[name] = {"checks": out, "why": why, "version": mf.get("version")}
json.dump(res, open(sys.argv[2], "w"), indent=1)
# summary
print("plugin".ljust(18), " ".join(c.ljust(3) for c in checks))
tot = {c: [0,0,0] for c in checks}
for n, v in sorted(res.items()):
    row = []
    for c in checks:
        x = v["checks"].get(c, "na")
        row.append({"pass":"ok ","na":" . ","FAIL":"XX "}[x])
        tot[c][{"pass":0,"na":1,"FAIL":2}[x]] += 1
    print(n.ljust(18), " ".join(row))
print("FAILS".ljust(18), " ".join(str(tot[c][2]).ljust(3) for c in checks))

# ---- markdown report ----
if len(sys.argv) > 3:
    import datetime
    names = {"K1":"one version source","K3":"identity/catalogue/tag/embedded agree","K4":"has tests","K5":"CI and licence","K7":"secrets redacted","K8":"User-Agent","K9":"timeout, shared client","K10":"429 handling","K11":"no empty catch","K12":"doc page and catalogue line","B1":"icon","B2":"brand colours","B4":"description","B5":"fixMatchHint","B6":"task text","X1":"settings schema","X2":"setting help text","X3":"secrets are Password","M2":"image method safe","C4":"auth exception","Q9":"alternate names"}
    L = [f"# Plugin compliance record ({datetime.date.today().isoformat()})", "",
         "Generated by `scripts/survey-plugins.py` then `scripts/audit-plugins.py` from the source of every real plugin repo, against the",
         "checks in `docs/PLUGIN_NEW_TYPE_CHECKLIST.md` that can be judged from files. `ok` = passes, `XX` = fails, `.` = not applicable.",
         "**Static only.** Every runtime and on-screen check (P1-P15, Q1-Q13 typed searches, X6, X8, M1, S1 live fixtures, T1 declared-identically,",
         "G3 verification pass) is unverified by this record and is listed in the last section.", "",
         "| plugin | " + " | ".join(checks) + " |", "|---|" + "---|" * len(checks)]
    for n, v in sorted(res.items()):
        L.append(f"| {n} | " + " | ".join({"pass":"ok","na":".","FAIL":"**XX**"}[v['checks'].get(c,'na')] for c in checks) + " |")
    L += ["", "| check | meaning |", "|---|---|"] + [f"| {c} | {names[c]} |" for c in checks]
    L += ["", "## Failures, with the reason the script found", ""]
    for n, v in sorted(res.items()):
        f = [(c, v['why'].get(c, '')) for c in checks if v['checks'].get(c) == "FAIL"]
        if not f: L += [f"### {n}", "No static failures.", ""]; continue
        L += [f"### {n}"] + [f"- **{c}** ({names[c]}): {w}" for c, w in f] + [""]
    open(sys.argv[3], "w", encoding="utf8").write("\n".join(L))
