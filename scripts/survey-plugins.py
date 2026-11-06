import json, os, re, glob, subprocess, collections, sys
root = r"W:\Scripts"
repos = sorted(d for d in os.listdir(root) if d.startswith("Chronicle.Plugin."))
rows = []
def rd(p):
    try: return open(p, encoding="utf8", errors="ignore").read()
    except Exception: return ""
for r in repos:
    p = os.path.join(root, r)
    row = {"repo": r}
    mf = os.path.join(p, "manifest.json")
    m = {}
    if os.path.exists(mf):
        try: m = json.loads(rd(mf))
        except Exception as e: row["manifest_error"] = str(e)[:60]
    row["has_manifest"] = bool(m)
    for k in ["plugin_id","name","version","author","description","min_chronicle_version","entry_type","iconUrl","brandColorLight","brandColorDark","fixMatchHint","supported_media_types","background_tasks","settings","license","homepage","github_repo"]:
        row["m_"+k] = k in m and m[k] not in (None, "", [])
    row["n_tasks"] = len(m.get("background_tasks", []) or [])
    row["n_settings_manifest"] = len(m.get("settings", []) or [])
    row["types"] = m.get("supported_media_types", [])
    row["version"] = m.get("version")
    cs = glob.glob(os.path.join(p, "*.cs")) + glob.glob(os.path.join(p, "**", "*.cs"), recursive=True)
    cs = [c for c in set(cs) if "\\obj\\" not in c and "\\bin\\" not in c and "\\tests\\" not in c.lower() and "\\tools\\" not in c.lower()]
    src = "\n".join(rd(c) for c in cs)
    row["loc"] = src.count("\n")
    for name, pat in {
        "IMetadataProvider": r"\bIMetadataProvider\b", "IImportProvider": r"\bIImportProvider\b",
        "IThemeProvider": r"\bITheme\w*\b", "IWidget": r"\bIWidget\w*\b", "IScanner": r"\bI\w*Scan\w*\b",
        "GetSettingsSchema": r"GetSettingsSchema", "PluginSettingsSchema_Password": r"SettingType\.Password",
        "Notice": r"SettingType\.Notice", "SettingDefaults": r"DefaultValue\s*=",
        "GetImageAsync": r"GetImageAsync", "ImageAllowlist": r"(Allow(ed)?Hosts?|ImageHosts|AllowedImageHosts)",
        "HealthCheck": r"HealthCheckAsync", "PluginAuthException": r"PluginAuthException",
        "HttpClientStatic": r"static\s+(readonly\s+)?HttpClient", "HttpClientNew": r"new\s+HttpClient\s*\(",
        "UserAgent": r"User-?Agent", "RateLimit": r"(RateLimit|Limiter|SemaphoreSlim|Task\.Delay)",
        "Breaker": r"Breaker", "Retry429": r"(429|TooManyRequests)", "RetryAfter": r"Retry-?After",
        "Cache": r"(TtlCache|MemoryCache|ConcurrentDictionary)",
        "CancellationToken": r"CancellationToken", "Redact": r"[Rr]edact",
        "SearchAsync": r"SearchAsync", "GetByIdAsync": r"GetByIdAsync",
        "AlternateNames": r"AlternateNames", "KnownExternalIds": r"KnownExternalIds",
        "IsRealHierarchyPosition": r"IsRealHierarchyPosition",
        "GetAcceptedCrossRefPrefixes": r"GetAcceptedCrossRefPrefixes", "ScoredCandidate": r"ScoredCandidate",
        "SupportedFields": r"SupportedFields", "EnrichOnly": r"EnrichOnly", "DefaultPriority": r"DefaultPriority",
        "GetRefreshedSettings": r"GetRefreshedSettings", "Configure": r"\bConfigure\(",
        "JsonRawKept": r"(RawJson|raw)\b", "Take(": r"\.Take\(\d+\)", "Truncate": r"(Substring\(0|Truncate|MaxLength)",
        "Skip_filter": r"\.Where\(", "ExtendedData": r"ExtendedData", "CastHeading": r"CastHeading",
        "InteractionVerb": r"InteractionVerb", "Aliases": r"Aliases\s*=", "HttpsOnly": r"Uri\.UriSchemeHttps",
        "Throws_InvalidOperation": r"InvalidOperationException", "HttpRequestException": r"HttpRequestException",
    }.items():
        row["c_"+name] = len(re.findall(pat, src))
    csproj = glob.glob(os.path.join(p, "*.csproj"))
    cp = rd(csproj[0]) if csproj else ""
    row["csproj_version"] = (re.search(r"<Version>([^<]+)", cp) or [None, None])[1]
    row["version_match"] = row["csproj_version"] == row["version"]
    row["tfm"] = (re.search(r"<TargetFramework>([^<]+)", cp) or [None, None])[1]
    row["nullable"] = "<Nullable>enable" in cp
    row["tests_dir"] = os.path.isdir(os.path.join(p, "tests")) or os.path.isdir(os.path.join(p, "Tests"))
    tests = glob.glob(os.path.join(p, "tests", "**", "*.cs"), recursive=True)
    tests = [t for t in tests if "\\obj\\" not in t and "\\bin\\" not in t]
    tsrc = "\n".join(rd(t) for t in tests)
    row["n_tests"] = len(re.findall(r"\[(Fact|Theory)", tsrc))
    row["fixtures"] = os.path.isdir(os.path.join(p, "tests", "fixtures")) or bool(glob.glob(os.path.join(p, "tests", "**", "fixtures"), recursive=True))
    row["readme"] = os.path.exists(os.path.join(p, "README.md"))
    row["license"] = bool(glob.glob(os.path.join(p, "LICENSE*")))
    row["ci"] = os.path.isdir(os.path.join(p, ".github", "workflows"))
    row["gitignore"] = os.path.exists(os.path.join(p, ".gitignore"))
    row["is_git"] = os.path.isdir(os.path.join(p, ".git"))
    if row["is_git"]:
        def g(*a):
            try: return subprocess.run(["git","-C",p,*a],capture_output=True,text=True,timeout=20).stdout.strip()
            except Exception: return ""
        row["remote"] = bool(g("remote"))
        row["tags"] = len(g("tag").split()) if g("tag") else 0
        row["last_commit"] = g("log","-1","--format=%cs")
        row["commits"] = int(g("rev-list","--count","HEAD") or 0)
    docs = os.path.join(root, "Chronicle", "docs", "plugins")
    row["doc_page"] = bool(glob.glob(os.path.join(docs, "PLUGIN_"+r.replace("Chronicle.Plugin.","").upper().replace(".","")+"*.md"))) or bool(glob.glob(os.path.join(docs, "PLUGIN_"+r.replace("Chronicle.Plugin.","")+"*.md")))
    pid = (m.get("plugin_id") or "").lower()
    row["plugin_id"] = pid
    row["in_builtin"] = os.path.isdir(os.path.join(root, "Chronicle", "src", "Chronicle.API", "plugins", pid)) if pid else False
    rows.append(row)
json.dump(rows, open(sys.argv[1], "w"), indent=1)
print(len(rows), "repos surveyed")
