# Chronicle Development Guide

**Version:** 1.0  
**Last Updated:** 2026-01-12  
**Author:** Michael Beck with Anthropic Claude

---

## Welcome Contributors

Thank you for your interest in contributing to Chronicle! This guide will help you get started with development, understand our workflows, and make meaningful contributions.

**Project Values:**
- **Quality over speed** - We take time to do things right
- **User-first** - Features serve real user needs
- **Open & collaborative** - Everyone's input matters
- **Comprehensive documentation** - Code should be understandable
- **Testing matters** - Features aren't done until tested

---

## Getting Started

### Prerequisites

**Required:**
- .NET SDK 8.0+
- Node.js 18+ (for frontend)
- Git
- Your favorite IDE (Visual Studio, VS Code, Rider)

**Recommended:**
- Docker Desktop (for testing)
- PostgreSQL (for testing)
- Postman/Insomnia (for API testing)

### Development Environment Setup

**1. Clone Repository:**
```bash
git clone https://github.com/chronicle/chronicle.git
cd chronicle
```

**2. Backend Setup:**
```bash
cd src/Chronicle.API
dotnet restore
dotnet build
```

**3. Frontend Setup:**
```bash
cd src/Chronicle.Web
npm install
npm run dev
```

**4. Database Setup:**
```bash
# SQLite (default - automatic)
dotnet ef database update

# OR PostgreSQL (optional)
docker run -d \
  --name chronicle-postgres \
  -e POSTGRES_DB=chronicle_dev \
  -e POSTGRES_USER=chronicle \
  -e POSTGRES_PASSWORD=dev_password \
  -p 5432:5432 \
  postgres:15
```

**5. Run Chronicle:**
```bash
# Terminal 1 - Backend
cd src/Chronicle.API
dotnet run

# Terminal 2 - Frontend
cd src/Chronicle.Web
npm run dev
```

**6. Access:**
```
Frontend: http://localhost:3000
Backend API: http://localhost:8080
Swagger: http://localhost:8080/swagger
```

---

## Project Structure

```
chronicle/
├── src/
│   ├── Chronicle.Core/           # Domain models, interfaces
│   ├── Chronicle.Data/            # Database, repositories
│   ├── Chronicle.Services/        # Business logic
│   ├── Chronicle.API/             # REST API (ASP.NET Core)
│   ├── Chronicle.Plugins/         # Plugin system
│   ├── Chronicle.Plugins.TMDB/    # Example plugin
│   ├── Chronicle.Updater/         # Update service
│   └── Chronicle.Web/             # React frontend
├── tests/
│   ├── Chronicle.Tests.Unit/      # Unit tests
│   ├── Chronicle.Tests.Integration/ # Integration tests
│   └── Chronicle.Tests.E2E/       # End-to-end tests
├── docs/                          # Documentation
├── scripts/                       # Build/deployment scripts
├── .github/
│   └── workflows/                 # GitHub Actions
├── docker/                        # Docker configs
└── README.md
```

---

## Git Workflow

### Branch Strategy

**Main Branches:**
- `main` - Production-ready code
- `develop` - Integration branch for next release
- `feature/*` - New features
- `bugfix/*` - Bug fixes
- `hotfix/*` - Urgent production fixes
- `release/*` - Release preparation

**Branch Naming:**
```
feature/rewatch-sessions
feature/version-management
bugfix/scrobble-duplicate-entries
hotfix/critical-security-patch
release/v1.2.0
```

### Workflow Process

**1. Create Feature Branch:**
```bash
git checkout develop
git pull origin develop
git checkout -b feature/my-new-feature
```

**2. Make Changes:**
```bash
# Make your changes
git add .
git commit -m "feat: add rewatch session support"
```

**3. Keep Updated:**
```bash
git fetch origin
git rebase origin/develop
```

**4. Push Branch:**
```bash
git push origin feature/my-new-feature
```

**5. Create Pull Request:**
- Go to GitHub
- Create PR from `feature/my-new-feature` → `develop`
- Fill out PR template
- Request reviews

**6. Address Review Feedback:**
```bash
# Make changes based on feedback
git add .
git commit -m "refactor: improve session query performance"
git push origin feature/my-new-feature
```

**7. Merge:**
- Maintainer merges PR when approved
- Branch is automatically deleted

---

## Commit Message Convention

### Format

```
<type>(<scope>): <subject>

<body>

<footer>
```

### Types

- **feat:** New feature
- **fix:** Bug fix
- **docs:** Documentation changes
- **style:** Code style changes (formatting, no logic change)
- **refactor:** Code refactoring (no feature/bug change)
- **perf:** Performance improvements
- **test:** Adding/updating tests
- **chore:** Build process, dependency updates
- **ci:** CI/CD changes

### Examples

**Simple:**
```
feat: add rewatch session support
```

**With Scope:**
```
fix(scrobbler): prevent duplicate entries
```

**With Body:**
```
feat(api): add version management endpoints

- GET /api/v1/media-groups/{id}/versions
- POST /api/v1/media/{id}/preferred
- Includes tests and documentation

Closes #123
```

**Breaking Change:**
```
feat(api): redesign scrobble endpoint

BREAKING CHANGE: Scrobble endpoint now requires media_type field

Old: POST /api/v1/scrobble { "media_id": 123 }
New: POST /api/v1/scrobble { "media_type": "tv", "media_id": 123 }

Closes #456
```

---

## Coding Standards

### C# (.NET Backend)

**Style Guide:**
- Follow [Microsoft C# Coding Conventions](https://docs.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- Use .editorconfig (included in repo)
- PascalCase for public members
- camelCase for private fields (with `_` prefix)
- 4 spaces for indentation

**Example:**
```csharp
public class MediaService
{
    private readonly ILogger<MediaService> _logger;
    private readonly IMediaRepository _repository;

    public MediaService(
        ILogger<MediaService> logger,
        IMediaRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    public async Task<MediaMetadata> GetMetadataAsync(int mediaId)
    {
        _logger.Info($"Fetching metadata for media ID: {mediaId}");
        
        var media = await _repository.GetByIdAsync(mediaId);
        
        if (media == null)
        {
            throw new MediaNotFoundException(mediaId);
        }
        
        return media;
    }
}
```

**Async/Await:**
- Always use async/await for I/O operations
- Suffix async methods with `Async`
- Never block on async code (`Task.Result`, `.Wait()`)

**Dependency Injection:**
- Use constructor injection
- Register services in `Program.cs`
- Use interfaces for dependencies

**Error Handling:**
```csharp
try
{
    await _service.DoSomethingAsync();
}
catch (MediaNotFoundException ex)
{
    _logger.Error("Media not found", ex);
    return NotFound(new ErrorResponse(ex));
}
catch (Exception ex)
{
    _logger.Fatal("Unexpected error", ex);
    return StatusCode(500, new ErrorResponse("Internal server error"));
}
```

### TypeScript (React Frontend)

**Style Guide:**
- ESLint + Prettier (configured in repo)
- 2 spaces for indentation
- Single quotes for strings
- Semicolons required
- PascalCase for components
- camelCase for functions/variables

**Example:**
```typescript
interface MediaCardProps {
  media: Media;
  onAdd: (mediaId: number) => void;
}

export const MediaCard: React.FC<MediaCardProps> = ({ media, onAdd }) => {
  const [isHovered, setIsHovered] = useState(false);

  const handleClick = () => {
    onAdd(media.id);
  };

  return (
    <div
      className="media-card"
      onMouseEnter={() => setIsHovered(true)}
      onMouseLeave={() => setIsHovered(false)}
      onClick={handleClick}
    >
      <img src={media.posterUrl} alt={media.title} />
      <h3>{media.title}</h3>
      {isHovered && <QuickActions media={media} />}
    </div>
  );
};
```

**Hooks:**
- Use functional components with hooks
- Custom hooks start with `use` prefix
- Extract complex logic into custom hooks

**State Management:**
- React Context for global state
- Local state for component-specific data
- Consider Redux Toolkit for complex state (if needed)

**API Calls:**
```typescript
export const useMedia = (mediaId: number) => {
  const [media, setMedia] = useState<Media | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<Error | null>(null);

  useEffect(() => {
    const fetchMedia = async () => {
      try {
        const response = await api.get(`/media/${mediaId}`);
        setMedia(response.data);
      } catch (err) {
        setError(err as Error);
      } finally {
        setLoading(false);
      }
    };

    fetchMedia();
  }, [mediaId]);

  return { media, loading, error };
};
```

### SQL (Database)

**Naming Conventions:**
- `snake_case` for all identifiers
- Plural table names (`users`, `media_items`)
- Descriptive column names (`created_at`, not `ca`)
- Foreign keys: `{table}_id` (e.g., `user_id`)

**Indexes:**
```sql
-- Always index foreign keys
CREATE INDEX idx_interaction_events_user_id 
    ON interaction_events(user_id);

-- Composite indexes for common queries
CREATE INDEX idx_media_items_type_year 
    ON media_items(media_type_id, year);
```

**Migrations:**
- One migration per logical change
- Include both `Up` and `Down` methods
- Test migrations before committing
- Never edit committed migrations

---

## Testing Requirements

### Test Coverage Goals

- **Unit Tests:** 80%+ coverage
- **Integration Tests:** All API endpoints
- **E2E Tests:** Critical user flows

### Unit Tests

**Location:** `tests/Chronicle.Tests.Unit/`

**Example:**
```csharp
public class MediaServiceTests
{
    private readonly Mock<IMediaRepository> _mockRepository;
    private readonly Mock<ILogger<MediaService>> _mockLogger;
    private readonly MediaService _service;

    public MediaServiceTests()
    {
        _mockRepository = new Mock<IMediaRepository>();
        _mockLogger = new Mock<ILogger<MediaService>>();
        _service = new MediaService(_mockLogger.Object, _mockRepository.Object);
    }

    [Fact]
    public async Task GetMetadataAsync_ValidId_ReturnsMedia()
    {
        // Arrange
        var expectedMedia = new Media { Id = 1, Title = "Blade Runner" };
        _mockRepository
            .Setup(r => r.GetByIdAsync(1))
            .ReturnsAsync(expectedMedia);

        // Act
        var result = await _service.GetMetadataAsync(1);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1, result.Id);
        Assert.Equal("Blade Runner", result.Title);
    }

    [Fact]
    public async Task GetMetadataAsync_InvalidId_ThrowsException()
    {
        // Arrange
        _mockRepository
            .Setup(r => r.GetByIdAsync(999))
            .ReturnsAsync((Media)null);

        // Act & Assert
        await Assert.ThrowsAsync<MediaNotFoundException>(
            () => _service.GetMetadataAsync(999)
        );
    }
}
```

**Run Tests:**
```bash
dotnet test
```

### Integration Tests

**Location:** `tests/Chronicle.Tests.Integration/`

**Example:**
```csharp
public class ScrobbleApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ScrobbleApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Scrobble_ValidRequest_Returns200()
    {
        // Arrange
        var request = new ScrobbleRequest
        {
            MediaType = "tv",
            MediaId = 12345,
            ProgressPercent = 45.5
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/scrobble", request);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ScrobbleResponse>();
        Assert.NotNull(result);
        Assert.True(result.Success);
    }
}
```

### End-to-End Tests

**Location:** `tests/Chronicle.Tests.E2E/`

**Framework:** Playwright

**Example:**
```typescript
test('user can scrobble media', async ({ page }) => {
  // Login
  await page.goto('http://localhost:3000');
  await page.fill('[name="username"]', 'testuser');
  await page.fill('[name="password"]', 'password');
  await page.click('button[type="submit"]');

  // Search for media
  await page.fill('[placeholder="Search..."]', 'Blade Runner');
  await page.click('text=Blade Runner (1982)');

  // Add to library
  await page.click('text=Add to Library');
  await expect(page.locator('text=Added to library')).toBeVisible();

  // Start watching
  await page.click('text=Play');
  await page.waitForTimeout(5000); // Simulate watching
  
  // Verify scrobble
  await page.goto('http://localhost:3000/history');
  await expect(page.locator('text=Blade Runner')).toBeVisible();
});
```

**Run E2E Tests:**
```bash
cd tests/Chronicle.Tests.E2E
npm test
```

---

## Pull Request Process

### PR Template

```markdown
## Description
Brief description of changes

## Type of Change
- [ ] Bug fix (non-breaking change which fixes an issue)
- [ ] New feature (non-breaking change which adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Documentation update

## Related Issues
Fixes #123
Relates to #456

## Testing
- [ ] Unit tests pass
- [ ] Integration tests pass
- [ ] Manual testing completed
- [ ] New tests added for new features

## Checklist
- [ ] Code follows style guidelines
- [ ] Self-reviewed code
- [ ] Commented complex code
- [ ] Updated documentation
- [ ] No new warnings
- [ ] Added tests
- [ ] All tests pass
- [ ] No merge conflicts

## Screenshots (if applicable)
```

### PR Review Checklist

**Code Quality:**
- [ ] Follows coding standards
- [ ] No code smells
- [ ] Appropriate abstractions
- [ ] No duplicated code
- [ ] Efficient algorithms

**Testing:**
- [ ] Tests included
- [ ] Tests pass
- [ ] Edge cases covered
- [ ] Error cases tested

**Documentation:**
- [ ] Code is self-documenting
- [ ] Complex logic explained
- [ ] API docs updated
- [ ] README updated if needed

**Security:**
- [ ] No hardcoded secrets
- [ ] Input validated
- [ ] SQL injection prevented
- [ ] XSS prevented
- [ ] CSRF protected

**Performance:**
- [ ] No N+1 queries
- [ ] Appropriate indexing
- [ ] Lazy loading used
- [ ] No blocking operations

### Approval Process

**Required Approvals:**
- 1 approval for minor changes
- 2 approvals for major features
- 3 approvals for breaking changes
- Maintainer approval always required

**Merge Strategy:**
- Squash and merge for feature branches
- Merge commit for release branches
- Rebase for hotfixes

---

## CI/CD Pipeline

### GitHub Actions Workflow

**On Push/PR:**
```yaml
name: CI

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main, develop ]

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '8.0.x'
      
      - name: Restore dependencies
        run: dotnet restore
      
      - name: Build
        run: dotnet build --no-restore
      
      - name: Test
        run: dotnet test --no-build --verbosity normal --collect:"XPlat Code Coverage"
      
      - name: Upload coverage
        uses: codecov/codecov-action@v3

  lint:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      
      - name: Setup Node
        uses: actions/setup-node@v3
        with:
          node-version: '18'
      
      - name: Install dependencies
        run: cd src/Chronicle.Web && npm ci
      
      - name: Lint
        run: cd src/Chronicle.Web && npm run lint
      
      - name: Type check
        run: cd src/Chronicle.Web && npm run type-check

  security:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      
      - name: Run CodeQL
        uses: github/codeql-action/init@v2
        with:
          languages: csharp, javascript
      
      - name: Autobuild
        uses: github/codeql-action/autobuild@v2
      
      - name: Perform CodeQL Analysis
        uses: github/codeql-action/analyze@v2
```

**On Release:**
```yaml
name: Release

on:
  push:
    tags:
      - 'v*'

jobs:
  build:
    strategy:
      matrix:
        os: [windows-latest, ubuntu-latest, macos-latest]
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v3
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
      
      - name: Build
        run: dotnet publish -c Release -r ${{ matrix.runtime }} --self-contained
      
      - name: Create artifact
        run: tar -czf chronicle-${{ matrix.os }}.tar.gz publish/
      
      - name: Upload artifact
        uses: actions/upload-artifact@v3
        with:
          name: chronicle-${{ matrix.os }}
          path: chronicle-${{ matrix.os }}.tar.gz

  docker:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      
      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v2
      
      - name: Login to DockerHub
        uses: docker/login-action@v2
        with:
          username: ${{ secrets.DOCKERHUB_USERNAME }}
          password: ${{ secrets.DOCKERHUB_TOKEN }}
      
      - name: Build and push
        uses: docker/build-push-action@v4
        with:
          push: true
          tags: chronicle/chronicle:${{ github.ref_name }},chronicle/chronicle:latest
```

---

## Release Process

### Versioning

**Semantic Versioning:** MAJOR.MINOR.PATCH

- **MAJOR:** Breaking changes
- **MINOR:** New features (backwards-compatible)
- **PATCH:** Bug fixes

**Examples:**
- `1.0.0` → `1.0.1` (bug fix)
- `1.0.1` → `1.1.0` (new feature)
- `1.1.0` → `2.0.0` (breaking change)

### Release Workflow

**1. Create Release Branch:**
```bash
git checkout develop
git pull origin develop
git checkout -b release/v1.2.0
```

**2. Update Version:**
```bash
# Update version in:
# - src/Chronicle.API/Chronicle.API.csproj
# - src/Chronicle.Web/package.json
# - CHANGELOG.md

git add .
git commit -m "chore: bump version to 1.2.0"
```

**3. Create PR to Main:**
```bash
git push origin release/v1.2.0
# Create PR: release/v1.2.0 → main
```

**4. After Merge, Tag Release:**
```bash
git checkout main
git pull origin main
git tag -a v1.2.0 -m "Release version 1.2.0"
git push origin v1.2.0
```

**5. Merge Back to Develop:**
```bash
git checkout develop
git merge main
git push origin develop
```

**6. Create GitHub Release:**
- Go to GitHub Releases
- Click "Draft a new release"
- Select tag `v1.2.0`
- Add release notes
- Attach build artifacts
- Publish release

### Release Notes Template

```markdown
## Chronicle v1.2.0

**Release Date:** 2026-01-12

### New Features
- ✨ Add rewatch session support (#123)
- ✨ Implement version management (#124)
- ✨ Add calendar view (#125)

### Improvements
- ⚡ Improve scrobble performance (#126)
- 📱 Better mobile responsiveness (#127)

### Bug Fixes
- 🐛 Fix duplicate scrobble entries (#128)
- 🐛 Correct timezone handling (#129)

### Breaking Changes
None

### Database Changes
- Add `watch_sessions` table
- Add `media_groups` table
- Migration required: Run `dotnet ef database update`

### Upgrade Notes
1. Backup your database
2. Stop Chronicle
3. Replace binaries
4. Run migrations
5. Start Chronicle

### Contributors
Thank you to all contributors: @user1, @user2, @user3
```

---

## Code of Conduct

### Our Pledge

We pledge to make participation in Chronicle a harassment-free experience for everyone, regardless of:
- Age
- Body size
- Disability
- Ethnicity
- Gender identity
- Level of experience
- Nationality
- Personal appearance
- Race
- Religion
- Sexual identity and orientation

### Our Standards

**Positive Behavior:**
- Using welcoming and inclusive language
- Being respectful of differing viewpoints
- Gracefully accepting constructive criticism
- Focusing on what's best for the community
- Showing empathy towards others

**Unacceptable Behavior:**
- Trolling, insulting, or derogatory comments
- Public or private harassment
- Publishing others' private information
- Other conduct which could reasonably be considered inappropriate

### Enforcement

Instances of unacceptable behavior may be reported to:
- Email: conduct@chronicle.app
- Direct message to maintainers

All complaints will be reviewed and investigated promptly and fairly.

---

## Getting Help

### Resources

- **Documentation:** [docs.chronicle.app](https://docs.chronicle.app)
- **GitHub Discussions:** [github.com/chronicle/chronicle/discussions](https://github.com/chronicle/chronicle/discussions)
- **Discord:** [discord.gg/chronicle](https://discord.gg/chronicle)
- **Email:** dev@chronicle.app

### Questions?

**Before Opening an Issue:**
1. Check existing issues
2. Search discussions
3. Review documentation
4. Ask in Discord

**When Opening an Issue:**
- Use issue templates
- Provide reproduction steps
- Include environment details
- Be respectful and patient

---

## Recognition

### Contributors

All contributors are recognized in:
- README.md contributors section
- Release notes
- About page in application
- Annual contributor spotlight

### Maintainers

Current maintainers:
- Michael Beck (@michaelbeck) - Lead Developer
- [To be added as project grows]

---

## License

Chronicle is released under the MIT License. By contributing, you agree that your contributions will be licensed under the same license.

See [LICENSE](LICENSE) file for details.

---

**Thank you for contributing to Chronicle!**

Your efforts help make Chronicle better for everyone. Whether you're fixing typos, reporting bugs, or building major features - every contribution matters.

**Happy coding! 🚀**

---

**Document Status:** Complete  
**Last Updated:** 2026-01-12
