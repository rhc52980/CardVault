import { useRef, useState, type ReactNode } from 'react'

interface Props {
  /** Null when the item has no artwork at all — renders the name placeholder instead. */
  image: string | null
  fallbackImage?: string | null
  name: string
  subtitle?: ReactNode
  badge?: ReactNode
  corner?: ReactNode
  footer?: ReactNode
  onClick?: () => void
}

/**
 * A single card in the grid. Tilts toward the pointer and moves a holo sheen with
 * it — the effect real cards have under a light, which makes a wall of flat images
 * feel like a binder page.
 */
export function CardTile({ image, fallbackImage, name, subtitle, badge, corner, footer, onClick }: Props) {
  const ref = useRef<HTMLDivElement>(null)
  const [loaded, setLoaded] = useState(false)
  const [failed, setFailed] = useState(false)
  const [broken, setBroken] = useState(false)

  function handleMove(e: React.MouseEvent<HTMLDivElement>) {
    const el = ref.current
    if (!el) return
    const rect = el.getBoundingClientRect()
    const px = (e.clientX - rect.left) / rect.width
    const py = (e.clientY - rect.top) / rect.height
    el.style.setProperty('--mx', `${px * 100}%`)
    el.style.setProperty('--my', `${py * 100}%`)
    el.style.transform =
      `perspective(900px) rotateX(${(0.5 - py) * 9}deg) rotateY(${(px - 0.5) * 11}deg) translateY(-6px) scale(1.03)`
  }

  function handleLeave() {
    const el = ref.current
    if (el) el.style.transform = ''
  }

  // The API image URL is the fallback if our own cache can't produce the file.
  const src = failed && fallbackImage ? fallbackImage : image

  // No artwork to request — don't fire a request that's guaranteed to 404.
  const showPlaceholder = broken || !src

  return (
    <div className="group">
      <div
        ref={ref}
        onMouseMove={handleMove}
        onMouseLeave={handleLeave}
        onClick={onClick}
        role={onClick ? 'button' : undefined}
        tabIndex={onClick ? 0 : undefined}
        onKeyDown={(e) => {
          if (onClick && (e.key === 'Enter' || e.key === ' ')) {
            e.preventDefault()
            onClick()
          }
        }}
        className="card-tile card-holo relative aspect-[245/342] cursor-pointer overflow-hidden rounded-xl bg-abyss ring-1 ring-white/5 focus:ring-2 focus:ring-arc focus:outline-none"
      >
        {!loaded && !showPlaceholder && <div className="skeleton absolute inset-0 rounded-xl" />}

        {showPlaceholder ? (
          // Hand-entered items often have no photo. Show the name rather than a
          // browser's broken-image glyph.
          <div className="flex h-full w-full items-center justify-center bg-gradient-to-br from-raised to-abyss p-3 text-center">
            <span className="line-clamp-4 text-xs font-medium text-mute">{name}</span>
          </div>
        ) : (
          <img
            src={src}
            alt={name}
            loading="lazy"
            decoding="async"
            onLoad={() => setLoaded(true)}
            onError={() => {
              if (!failed && fallbackImage) setFailed(true)
              else setBroken(true)
            }}
            className={`h-full w-full object-cover transition-opacity duration-300 ${loaded ? 'opacity-100' : 'opacity-0'}`}
          />
        )}

        {badge && <div className="absolute top-2 left-2 z-10">{badge}</div>}
        {corner && <div className="absolute top-2 right-2 z-10">{corner}</div>}

        {footer && (
          <div className="absolute inset-x-0 bottom-0 z-10 translate-y-full bg-gradient-to-t from-black/95 via-black/80 to-transparent p-2 transition-transform duration-200 group-hover:translate-y-0">
            {footer}
          </div>
        )}
      </div>

      <div className="mt-2 px-0.5">
        <div className="truncate text-sm font-medium text-bright" title={name}>
          {name}
        </div>
        {subtitle && <div className="mt-0.5 truncate text-xs text-mute">{subtitle}</div>}
      </div>
    </div>
  )
}
