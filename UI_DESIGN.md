# Chronicle UI Design

**Version:** 1.0  
**Last Updated:** 2026-01-12  
**Author:** Michael Beck with Anthropic Claude

---

## Design Philosophy

Chronicle's interface prioritizes:

1. **Familiarity** - Similar aesthetic to *arr apps (Sonarr, Radarr, Lidarr)
2. **Efficiency** - Quick access to common tasks
3. **Customization** - Users control what they see
4. **Clarity** - Information hierarchy is obvious
5. **Responsiveness** - Works on all screen sizes
6. **Accessibility** - Usable by everyone

---

## Visual Design

### Color Palette

**Dark Theme (Default):**
```css
--bg-primary: #1e1e1e;
--bg-secondary: #2d2d2d;
--bg-tertiary: #3a3a3a;
--text-primary: #e0e0e0;
--text-secondary: #b0b0b0;
--accent-primary: #5c7cfa;
--accent-hover: #748ffc;
--success: #51cf66;
--warning: #ffd43b;
--error: #ff6b6b;
```

**Light Theme:**
```css
--bg-primary: #ffffff;
--bg-secondary: #f8f9fa;
--bg-tertiary: #e9ecef;
--text-primary: #212529;
--text-secondary: #6c757d;
--accent-primary: #4263eb;
--accent-hover: #5c7cfa;
--success: #2f9e44;
--warning: #f59f00;
--error: #c92a2a;
```

### Typography

**Font Stack:**
```css
font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", 
             Roboto, "Helvetica Neue", Arial, sans-serif;
```

**Type Scale:**
- Heading 1: 32px / 2rem (bold)
- Heading 2: 24px / 1.5rem (semi-bold)
- Heading 3: 20px / 1.25rem (semi-bold)
- Body: 16px / 1rem (regular)
- Small: 14px / 0.875rem (regular)
- Tiny: 12px / 0.75rem (regular)

### Spacing

**Base Unit:** 8px

```css
--space-xs: 4px;
--space-sm: 8px;
--space-md: 16px;
--space-lg: 24px;
--space-xl: 32px;
--space-2xl: 48px;
```

---

## Layout Structure

### Overall Layout

```
┌─────────────────────────────────────────────────┐
│ Header                                          │
├──────┬──────────────────────────────────────────┤
│      │                                          │
│ Side │                                          │
│ bar  │           Main Content                   │
│      │                                          │
│      │                                          │
└──────┴──────────────────────────────────────────┘
```

### Header

**Height:** 64px

**Contents:**
- Chronicle logo (left)
- Search bar (center)
- Currently watching indicator (right)
- User menu (far right)

```
┌─────────────────────────────────────────────────────┐
│ [Logo] [Search.....................] [▶️] [👤]     │
└─────────────────────────────────────────────────────┘
```

### Sidebar

**Width:** 240px (collapsed: 64px)

**Contents:**
- Dashboard
- Movies
- TV Shows
- Music
- Books
- [Custom media types...]
- Statistics
- Calendar
- Settings

**Collapsible:**
- Click hamburger to collapse
- Icons only when collapsed
- Auto-collapse on mobile

### Main Content Area

**Max Width:** 1400px (centered)

**Padding:** 24px

**Responsive:**
- Desktop: Full width with max-width
- Tablet: Full width
- Mobile: Full width, reduced padding

---

## Page Layouts

### Dashboard

```
┌───────────────────────┬───────────────────────┐
│ Continue Watching     │ Upcoming Releases     │
│                       │                       │
├───────────────────────┴───────────────────────┤
│ Recent Activity                               │
│                                               │
├──────────────────┬────────────────────────────┤
│ Quick Stats      │ Friends Activity           │
│                  │                            │
└──────────────────┴────────────────────────────┘
```

**Widget Grid:**
- Drag & drop to rearrange
- Resize (small, medium, large, full-width)
- Add/remove widgets
- Multiple layouts (save/load)

### Library View

```
┌─────────────────────────────────────────────────┐
│ Movies                                   [Grid▼]│
│ [Filter▼] [Sort▼] [Search...]                  │
├─────────────────────────────────────────────────┤
│ ┌────┐ ┌────┐ ┌────┐ ┌────┐ ┌────┐            │
│ │    │ │    │ │    │ │    │ │    │            │
│ │ [1]│ │ [2]│ │ [3]│ │ [4]│ │ [5]│            │
│ └────┘ └────┘ └────┘ └────┘ └────┘            │
│ ┌────┐ ┌────┐ ┌────┐ ┌────┐ ┌────┐            │
│ │    │ │    │ │    │ │    │ │    │            │
│ │ [6]│ │ [7]│ │ [8]│ │ [9]│ │ 10 │            │
│ └────┘ └────┘ └────┘ └────┘ └────┘            │
└─────────────────────────────────────────────────┘
```

**View Options:**
- Grid (posters)
- List (detailed)
- Table (compact)

**Filters:**
- Status (watching, completed, plan-to-watch)
- Genre
- Year
- Rating
- Custom tags

### Media Detail Page

```
┌─────────────────────────────────────────────────┐
│ ┌────────┐  Blade Runner (1982)                 │
│ │        │  ⭐ 8.1  •  117 min  •  Sci-Fi       │
│ │ POSTER │                                      │
│ │        │  [+ Add to Library ▼]  [▶️ Play]     │
│ │        │                                      │
│ └────────┘  Description...                      │
│                                                 │
│ ┌─────────────────────────────────────────────┐ │
│ │ Versions                                    │ │
│ │ • Theatrical Cut (1982)                     │ │
│ │ • Director's Cut (1992)                     │ │
│ │ • Final Cut (2007) ⭐ Preferred             │ │
│ └─────────────────────────────────────────────┘ │
│                                                 │
│ Cast • Crew • Similar • Reviews                 │
└─────────────────────────────────────────────────┘
```

### Statistics Page

```
┌─────────────────────────────────────────────────┐
│ Your Statistics                                 │
│                                                 │
│ ┌──────────┬──────────┬──────────┬──────────┐  │
│ │ Episodes │ Movies   │ Books    │ Watch    │  │
│ │  1,247   │   342    │    87    │  Time    │  │
│ │          │          │          │ 2,091hrs │  │
│ └──────────┴──────────┴──────────┴──────────┘  │
│                                                 │
│ Watch Time This Year                            │
│ ┌─────────────────────────────────────────────┐ │
│ │     📊 Graph/Chart                          │ │
│ └─────────────────────────────────────────────┘ │
│                                                 │
│ Top Genres • Most Watched • Completion Rate     │
└─────────────────────────────────────────────────┘
```

### Calendar View

```
┌─────────────────────────────────────────────────┐
│ Calendar                            [← Jan 26 →]│
│                                                 │
│  Sun   Mon   Tue   Wed   Thu   Fri   Sat       │
│  ───   ───   ───   ───   ───   ───   ───       │
│        [1]   [2]   [3]   [4]   [5]   [6]       │
│         📺   📺           📺                     │
│  [7]   [8]   [9]  [10]  [11]  [12]  [13]       │
│         📺    📺                                 │
│ [14]  [15]  [16]  [17]  [18]  [19]  [20]       │
│              📺    🎬    📺                      │
│ [21]  [22]  [23]  [24]  [25]  [26]  [27]       │
│                                                 │
└─────────────────────────────────────────────────┘
```

**Features:**
- Click date to see details
- Different icons per media type
- Toggle: Upcoming releases / Watch history
- Export to iCal

---

## Components

### Media Card

**Grid View:**
```
┌────────────┐
│            │
│   POSTER   │
│            │
│            │
├────────────┤
│ Title      │
│ 2024 • ⭐8 │
└────────────┘
```

**Hover State:**
- Dim overlay
- Quick actions (Play, Add, Info)
- Progress indicator if watching

**List View:**
```
┌──────┬────────────────────────────────────────┐
│ [IMG]│ Blade Runner (1982)                    │
│      │ Sci-Fi, Thriller • 117 min • ⭐ 8.1   │
│      │ Status: Watched • Rating: 9/10        │
└──────┴────────────────────────────────────────┘
```

### Progress Bar

```
┌──────────────────────────────────────┐
│ Breaking Bad - S01E05                │
│ ████████████░░░░░░░░░░░░░░░░  45%   │
│ 22m remaining                        │
└──────────────────────────────────────┘
```

**Variations:**
- Episode progress
- Season progress
- Overall series progress
- Book reading progress

### Status Badge

```
[Watching] [Completed] [Plan to Watch] [Dropped] [On Hold]
```

**Colors:**
- Watching: Blue
- Completed: Green
- Plan to Watch: Gray
- Dropped: Red
- On Hold: Yellow

### Rating Widget

**Display:**
```
⭐ 8.5 / 10
```

**Input (stars):**
```
☆ ☆ ☆ ☆ ☆ ☆ ☆ ☆ ☆ ☆  (hover to rate)
★ ★ ★ ★ ★ ★ ★ ★ ★ ☆  (9/10)
```

**Input (numeric):**
```
[8] / 10
```

### Search Results

```
┌─────────────────────────────────────────────────┐
│ Search: "blade runner"                    [✕]   │
├─────────────────────────────────────────────────┤
│ ┌────┐                                          │
│ │    │  Blade Runner (1982)                     │
│ │ [1]│  Movie • Sci-Fi • ⭐ 8.1                 │
│ └────┘                                          │
│ ┌────┐                                          │
│ │    │  Blade Runner 2049 (2017)                │
│ │ [2]│  Movie • Sci-Fi • ⭐ 8.0                 │
│ └────┘                                          │
│ ┌────┐                                          │
│ │    │  Blade Runner: Black Lotus (2021)        │
│ │ [3]│  TV Show • Anime • ⭐ 6.5                │
│ └────┘                                          │
└─────────────────────────────────────────────────┘
```

**Features:**
- Instant search (as-you-type)
- Media type filtering
- Jump to result (keyboard navigation)
- Recent searches

### Currently Watching Banner

```
┌─────────────────────────────────────────────────┐
│ ▶️ You're watching Blade Runner (32% • 19m left) │
└─────────────────────────────────────────────────┘
```

**States:**
- Active (currently playing)
- Paused (resume available)
- Inactive (cleared after 5 min)

### Notification Toast

```
┌─────────────────────────────────┐
│ ✓ Added to library              │
│ Blade Runner                    │
└─────────────────────────────────┘
```

**Types:**
- Success (green)
- Error (red)
- Warning (yellow)
- Info (blue)

**Duration:** 3-5 seconds, dismissible

---

## Widget System

### Widget Framework

**Widget Structure:**
```typescript
interface Widget {
  id: string;
  type: string;
  title: string;
  size: 'small' | 'medium' | 'large' | 'full';
  position: { row: number; col: number };
  settings: Record<string, any>;
}
```

**Widget Sizes:**
- **Small:** 1x1 grid cell
- **Medium:** 2x1 or 1x2 grid cells
- **Large:** 2x2 grid cells
- **Full:** Full width

### Built-In Widgets

**1. Recent Activity**
```
┌──────────────────────────────┐
│ Recent Activity              │
├──────────────────────────────┤
│ 📺 Breaking Bad S01E05       │
│    2 hours ago               │
│ 🎬 Blade Runner              │
│    Yesterday                 │
│ 📚 Dune                      │
│    3 days ago                │
└──────────────────────────────┘
```

**2. Continue Watching**
```
┌──────────────────────────────┐
│ Continue Watching            │
├──────────────────────────────┤
│ ┌────┐ Breaking Bad          │
│ │    │ S01E06                │
│ │ [1]│ ████████░░░░  60%    │
│ └────┘                       │
└──────────────────────────────┘
```

**3. Upcoming Releases**
```
┌──────────────────────────────┐
│ Upcoming This Week           │
├──────────────────────────────┤
│ Wed • The Mandalorian S03E05 │
│ Thu • New Movie Release      │
│ Fri • Album Drop             │
└──────────────────────────────┘
```

**4. Quick Stats**
```
┌──────────────────────────────┐
│ This Week                    │
├──────────────────────────────┤
│ 15 episodes                  │
│ 2 movies                     │
│ 22 hours                     │
└──────────────────────────────┘
```

**5. Calendar**
```
┌──────────────────────────────┐
│ January 2026                 │
├──────────────────────────────┤
│ M  T  W  T  F  S  S          │
│          1  2  3  4  5       │
│ 6  7  8  9 10 11 12          │
│    •     •                   │
└──────────────────────────────┘
```

### Widget Customization

**Dashboard Editor Mode:**
```
┌─────────────────────────────────────┐
│ [Exit Edit Mode]                    │
├─────────────────────────────────────┤
│ ┌──────────┐ ┌──────────┐          │
│ │ Widget 1 │ │ Widget 2 │          │
│ │   [⚙️]    │ │   [⚙️]    │          │
│ └──────────┘ └──────────┘          │
│                                     │
│ [+ Add Widget]                      │
└─────────────────────────────────────┘
```

**Features:**
- Drag to reorder
- Resize handles
- Settings gear per widget
- Add/remove widgets
- Save/load layouts
- Reset to defaults

**Widget Settings Modal:**
```
┌─────────────────────────────────────┐
│ Upcoming Releases Settings    [✕]   │
├─────────────────────────────────────┤
│ Days ahead: [7         ]            │
│                                     │
│ Media types:                        │
│ ☑ TV Shows                          │
│ ☑ Movies                            │
│ ☐ Music                             │
│                                     │
│         [Cancel]  [Save]            │
└─────────────────────────────────────┘
```

---

## Responsive Design

### Breakpoints

```css
/* Mobile */
@media (max-width: 640px) { ... }

/* Tablet */
@media (min-width: 641px) and (max-width: 1024px) { ... }

/* Desktop */
@media (min-width: 1025px) { ... }
```

### Mobile Layout

**Collapsed Sidebar:**
- Hamburger menu
- Full-screen overlay when opened
- Swipe to close

**Grid Adjustments:**
- 2 columns (portrait)
- 3 columns (landscape)
- Cards stack vertically in list view

**Touch Optimizations:**
- Larger tap targets (min 44px)
- Swipe gestures (back, refresh)
- Pull to refresh

### Tablet Layout

**Sidebar:**
- Collapsed by default
- Opens as slide-over panel
- Can pin open on landscape

**Grid:**
- 3-4 columns
- Hybrid grid/list view option

---

## Accessibility

### Keyboard Navigation

**Global Shortcuts:**
- `/` - Focus search
- `g d` - Go to dashboard
- `g m` - Go to movies
- `g t` - Go to TV shows
- `g s` - Go to statistics
- `?` - Show keyboard shortcuts

**Navigation:**
- `Tab` / `Shift+Tab` - Navigate elements
- `Enter` - Activate
- `Escape` - Close modal/cancel
- `Arrow keys` - Navigate grids/lists

### Screen Reader Support

**ARIA Labels:**
```html
<button aria-label="Add to library">
  <PlusIcon />
</button>

<div role="progressbar" 
     aria-valuenow="45" 
     aria-valuemin="0" 
     aria-valuemax="100">
  45% complete
</div>
```

**Semantic HTML:**
- Proper heading hierarchy
- `<nav>`, `<main>`, `<aside>` landmarks
- Alt text for images
- Form labels

### Color Contrast

**WCAG AA Compliance:**
- Text: 4.5:1 minimum contrast
- Large text (18pt+): 3:1 minimum
- UI components: 3:1 minimum

**High Contrast Mode:**
- Optional theme for vision impairment
- Increased contrast ratios
- Bold outlines on interactive elements

---

## Themes

### Built-In Themes

**1. Dark (Default)**
- Near-black backgrounds
- Muted accent colors
- Reduced eye strain

**2. Light**
- White/light gray backgrounds
- Vibrant accent colors
- High energy feel

**3. OLED Dark**
- True black background (#000000)
- Perfect for OLED screens
- Maximum power savings

**4. High Contrast**
- Maximum contrast ratios
- Bold outlines
- Accessibility-focused

### Custom Themes

**Theme Editor:**
```
┌─────────────────────────────────────┐
│ Theme Editor                  [✕]   │
├─────────────────────────────────────┤
│ Theme Name: [My Custom Theme]       │
│                                     │
│ Colors:                             │
│ Background:     [#1e1e1e] 🎨        │
│ Text:           [#e0e0e0] 🎨        │
│ Accent:         [#5c7cfa] 🎨        │
│ Success:        [#51cf66] 🎨        │
│ Warning:        [#ffd43b] 🎨        │
│ Error:          [#ff6b6b] 🎨        │
│                                     │
│ Preview: [████████████████████]     │
│                                     │
│    [Export]  [Cancel]  [Save]       │
└─────────────────────────────────────┘
```

**Import/Export:**
- JSON theme files
- Share with community
- Install from URL

---

## Animations

### Principles

- **Purposeful** - Animations serve a function
- **Subtle** - Not distracting
- **Fast** - 150-300ms duration
- **Respectful** - Honor prefers-reduced-motion

### Common Animations

**Fade In:**
```css
@keyframes fadeIn {
  from { opacity: 0; }
  to { opacity: 1; }
}
```

**Slide In:**
```css
@keyframes slideIn {
  from { transform: translateX(-20px); opacity: 0; }
  to { transform: translateX(0); opacity: 1; }
}
```

**Loading Spinner:**
```css
@keyframes spin {
  to { transform: rotate(360deg); }
}
```

**Reduced Motion:**
```css
@media (prefers-reduced-motion: reduce) {
  * {
    animation-duration: 0.01ms !important;
    transition-duration: 0.01ms !important;
  }
}
```

---

## Modals & Overlays

### Modal Structure

```
┌─────────────────────────────────────┐
│ ┌───────────────────────────────┐   │
│ │ Modal Title             [✕]   │   │
│ ├───────────────────────────────┤   │
│ │                               │   │
│ │ Modal content here...         │   │
│ │                               │   │
│ ├───────────────────────────────┤   │
│ │        [Cancel]  [Confirm]    │   │
│ └───────────────────────────────┘   │
└─────────────────────────────────────┘
```

**Behaviors:**
- Click outside to close (optional)
- Escape key to close
- Focus trap (Tab cycles within modal)
- Return focus to trigger element on close

### Drawer (Side Panel)

```
┌────────────────────────────┬────────┐
│                            │        │
│   Main Content             │ Drawer │
│                            │        │
│                            │        │
└────────────────────────────┴────────┘
```

**Use Cases:**
- Filters
- Settings
- Quick info
- Related content

---

## Loading States

### Skeleton Screens

```
┌────────────────────────────────────┐
│ ┌────┐                             │
│ │▓▓▓▓│  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓             │
│ │▓▓▓▓│  ▓▓▓▓▓▓  ▓▓▓▓               │
│ └────┘                             │
│ ┌────┐                             │
│ │▓▓▓▓│  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓             │
│ │▓▓▓▓│  ▓▓▓▓▓▓  ▓▓▓▓               │
│ └────┘                             │
└────────────────────────────────────┘
```

**Use For:**
- Initial page load
- Lazy loading
- Data fetching

### Loading Spinner

```
  ⟲  Loading...
```

**Use For:**
- Button actions
- Small operations
- Inline loading

### Progress Bar

```
████████████░░░░░░░░░░░░  45%
```

**Use For:**
- File uploads
- Import/export
- Long operations

---

## Empty States

### No Results

```
┌─────────────────────────────────────┐
│                                     │
│           🔍                         │
│     No results found                │
│                                     │
│  Try adjusting your filters         │
│                                     │
└─────────────────────────────────────┘
```

### Empty Library

```
┌─────────────────────────────────────┐
│                                     │
│           📚                         │
│   Your library is empty             │
│                                     │
│  [+ Add Your First Item]            │
│                                     │
└─────────────────────────────────────┘
```

---

## Error States

### Error Message

```
┌─────────────────────────────────────┐
│ ⚠️  Something went wrong            │
│                                     │
│ Failed to load media information.   │
│                                     │
│ [Try Again]  [Report Issue]         │
└─────────────────────────────────────┘
```

### Inline Error

```
┌─────────────────────────────────────┐
│ API Key: [____________]             │
│ ❌ Invalid API key format           │
└─────────────────────────────────────┘
```

---

## Forms

### Input Fields

```
┌─────────────────────────────────────┐
│ Username                            │
│ [michael____________]               │
│                                     │
│ Email                               │
│ [michael@example.com]               │
│                                     │
│ Password                            │
│ [••••••••••••••]  👁️               │
└─────────────────────────────────────┘
```

**States:**
- Default
- Focus (blue border)
- Error (red border + message)
- Disabled (grayed out)
- Success (green border + checkmark)

### Buttons

**Primary:**
```
┌──────────┐
│ Continue │  (Blue, bold)
└──────────┘
```

**Secondary:**
```
┌──────────┐
│  Cancel  │  (Gray outline)
└──────────┘
```

**Danger:**
```
┌──────────┐
│  Delete  │  (Red)
└──────────┘
```

**Icon Button:**
```
[⚙️]  [🗑️]  [✏️]
```

---

## Data Visualization

### Charts

**Line Chart:**
```
Watch Time Over Time

    |        ╱╲
    |       ╱  ╲     ╱╲
    |      ╱    ╲   ╱  ╲
    |     ╱      ╲ ╱    ╲
    |____╱________╲______╲___
     Jan  Feb  Mar  Apr  May
```

**Bar Chart:**
```
Episodes by Genre

Sci-Fi    ████████████ 245
Drama     ██████████ 198
Comedy    ████████ 156
Action    ██████ 112
```

**Pie Chart:**
```
        Media Types
    
       🎬 45%
    📺      📚
    35%    20%
```

---

## Best Practices

### Performance

- Lazy load images
- Virtual scrolling for large lists
- Debounce search input
- Cache API responses
- Optimize bundle size

### UX Patterns

- Provide feedback for all actions
- Show loading states
- Clear error messages
- Undo destructive actions
- Save state automatically

### Mobile-First

- Design for smallest screen first
- Progressive enhancement
- Touch-friendly targets
- Avoid hover-only interactions

---

**Document Status:** Complete  
**Implementation Priority:** Phase 1 (Core UI), Phase 2 (Widgets, themes)
