import { useQuery } from '@tanstack/react-query'
import { listPlugins } from '@/api/plugins'
import { getPluginDisplayOrder } from '@/api/settings'
import { PluginFold } from '@/components/PluginFold'
import { PluginMetadataBox } from '@/components/PluginMetadataBox'
import type { MediaItem } from '@/types'

/**
 * The collapsible per-plugin metadata panels of an item (TMDB, IMDb, Wikipedia, MusicBrainz, Hardcover, Open Library ...),
 * each with its own Refresh / Fix Match / Clear Match. The same rules as the media detail page: only plugins that are
 * enabled and support the item's type, in that type's saved display order. A supporting plugin shows even with no data yet.
 */
export function ItemPluginPanels({ item }: { item: MediaItem }) {
  const { data: plugins = [] } = useQuery({ queryKey: ['plugins'], queryFn: listPlugins })
  const { data: order = {} } = useQuery({ queryKey: ['plugin-display-order'], queryFn: getPluginDisplayOrder })

  const typeKey = (item.mediaTypeInternalName ?? item.mediaTypeName ?? '').toLowerCase()
  // Every enabled plugin that supports this item's type gets its card, even before it has data or an enrichment row, so Fix Match
  // is always there and a card never vanishes because its row is missing at that moment (mid-refresh, or never attempted).
  const applicable = plugins.filter(p => p.isEnabled && p.supportedMediaTypes?.some(t => t.toLowerCase() === typeKey)).map(p => p.pluginId)
  const raw = new Set([...Object.keys(item.pluginMetadata ?? {}), ...Object.keys(item.enrichmentStatuses ?? {}), ...applicable])
  const ordered = order[typeKey] ?? []
  const ids = [...ordered.filter(id => raw.has(id)), ...[...raw].filter(id => !ordered.includes(id))]

  return (
    <>
      {ids.map(pluginId => {
        const plugin = plugins.find(p => p.pluginId === pluginId)
        if (plugin && !plugin.isEnabled) return null
        if (plugin?.supportedMediaTypes?.length && !plugin.supportedMediaTypes.some(t => t.toLowerCase() === typeKey)) return null
        return (
          <PluginFold key={`${item.id}-${pluginId}`} foldKey={`media.${item.id}.${pluginId}`} label={plugin?.name ?? pluginId} iconUrl={plugin?.iconUrl}>
            <PluginMetadataBox
              mediaId={item.id}
              pluginId={pluginId}
              pluginName={plugin?.name ?? pluginId}
              iconUrl={plugin?.iconUrl}
              fixMatchHint={plugin?.fixMatchHint}
              sourceUrls={plugin?.sourceUrls}
              metadata={item.pluginMetadata?.[pluginId]}
              enrichmentStatus={item.enrichmentStatuses?.[pluginId]}
              enrichmentConflict={item.enrichmentConflicts?.[pluginId]}
              externalIds={item.externalIds ?? []}
              refreshLogs={item.refreshLogs}
            />
          </PluginFold>
        )
      })}
    </>
  )
}
