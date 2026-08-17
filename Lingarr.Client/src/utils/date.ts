/**
 * Formats a date string into a more readable format.
 * @example
 * formatDate("2025-01-01") // "1 Jan 2025"
 */
export const formatDate = (dateString: string | Date) => {
    const options: Intl.DateTimeFormatOptions = {
        year: 'numeric',
        month: 'short',
        day: 'numeric'
    }

    const locale = navigator.language || 'en-US'
    return new Date(dateString).toLocaleDateString(locale, options)
}

/**
 * Formats a date string into a more readable format.
 * @example
 * formatDate("2025-01-01") // "1 Jan 2025"
 */
export const formatDateTime = (dateString: string | Date) => {
    const options: Intl.DateTimeFormatOptions = {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
        hour: 'numeric',
        minute: 'numeric'
    }

    const locale = navigator.language || 'en-US'
    return new Date(dateString).toLocaleDateString(locale, options)
}

/**
 * Formats the elapsed time between two dates.
 * @example
 * formatDuration("2025-01-01T00:00:00", "2025-01-01T02:05:30") // "2h 5m 30s"
 */
export const formatDuration = (start: string | Date, end: string | Date) => {
    const seconds = Math.max(
        0,
        Math.round((new Date(end).getTime() - new Date(start).getTime()) / 1000)
    )
    const h = Math.floor(seconds / 3600)
    const m = Math.floor((seconds % 3600) / 60)
    const s = seconds % 60
    return [h && `${h}h`, (h || m) && `${m}m`, `${s}s`].filter(Boolean).join(' ')
}
