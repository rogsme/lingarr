<template>
    <CardComponent title="Indexing">
        <template #description>
            The media indexing schedule controls the iteration with which Lingarr should sync with
            Sonarr and Radarr.
        </template>
        <template #content>
            <SaveNotification ref="saveNotification" />
            <div class="flex flex-col space-y-2 pb-4">
                <span class="font-semibold">Set movie indexer:</span>
                <InputComponent
                    v-model="movieSchedule"
                    label="Cron format: minute hour day month weekday (e.g., '0 * * * *' for hourly)"
                    :placeholder="'0 * * * *'"
                    :validation-type="INPUT_VALIDATION_TYPE.CRON"
                    @update:validation="(val) => (movieScheduleIsValid = val)" />
                <span class="font-semibold">Set tv show indexer:</span>
                <InputComponent
                    v-model="showSchedule"
                    label="Cron format: minute hour day month weekday (e.g., '0 * * * *' for hourly)"
                    :placeholder="'0 * * * *'"
                    :validation-type="INPUT_VALIDATION_TYPE.CRON"
                    @update:validation="(val) => (showScheduleIsValid = val)" />
            </div>
        </template>
    </CardComponent>

    <CardComponent title="Automation">
        <template #description>
            Set up automation. Note that if automation is implemented, you also need to configure
            the necessary
            <a class="cursor-pointer underline" @click="router.push({ name: 'services-settings' })">
                services
            </a>
            .
        </template>
        <template #content>
            <div class="flex flex-col space-y-4">
                <div class="flex items-center space-x-2">
                    <span>Automated translation:</span>
                    <ToggleButton v-model="automationEnabled">
                        <span class="text-sm font-medium text-primary-content">
                            {{ automationEnabled === 'true' ? 'Enabled' : 'Disabled' }}
                        </span>
                    </ToggleButton>
                </div>

                <span class="font-semibold">Set translation schedule:</span>
                <InputComponent
                    v-model="translationSchedule"
                    label="Cron format: minute hour day month weekday (e.g., '0 * * * *' for hourly)"
                    :placeholder="'0 * * * *'"
                    :validation-type="INPUT_VALIDATION_TYPE.CRON"
                    @update:validation="(val) => (translationScheduleIsValid = val)" />

                <span class="font-semibold">Limits:</span>
                <InputComponent
                    v-model="maxTranslationsPerRun"
                    :type="INPUT_TYPE.NUMBER"
                    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                    :min-length="0"
                    label="Limit the amount of translations per schedule"
                    @update:validation="(val) => (maxTranslationsPerRunIsValid = val)" />

                <span class="font-semibold">Default file age delay for translation:</span>
                <InputComponent
                    v-model="movieAgeThreshold"
                    :type="INPUT_TYPE.NUMBER"
                    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                    :min-length="0"
                    label="Movie file age delay in 'hours'"
                    @update:validation="(val) => (movieAgeThresholdIsValid = val)" />
                <InputComponent
                    v-model="showAgeThreshold"
                    :type="INPUT_TYPE.NUMBER"
                    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                    :min-length="0"
                    label="TV Show file age delay in 'hours'"
                    @update:validation="(val) => (showAgeThresholdIsValid = val)" />
            </div>
        </template>
    </CardComponent>

    <CardComponent title="Translation window">
        <template #description>
            Run translations continuously within a daily time window. This replaces the scheduled
            automation above; enabling one disables the other.
        </template>
        <template #content>
            <div class="flex flex-col space-y-4">
                <div class="flex items-center space-x-2">
                    <span>Translation window:</span>
                    <ToggleButton v-model="windowEnabled">
                        <span class="text-sm font-medium text-primary-content">
                            {{ windowEnabled === 'true' ? 'Enabled' : 'Disabled' }}
                        </span>
                    </ToggleButton>
                </div>

                <template v-if="windowEnabled === 'true'">
                    <span class="font-semibold">Timezone:</span>
                    <SelectComponent
                        :options="timezoneOptions"
                        :selected="windowTimezone"
                        placeholder="Select a timezone..."
                        @update:selected="(value: string) => (windowTimezone = value)" />
                    <span v-if="currentTimeInZone" class="text-sm">
                        Current time in {{ windowTimezone }}: {{ currentTimeInZone }}
                    </span>

                    <span class="font-semibold">Set translation window:</span>
                    <InputComponent
                        v-model="windowStart"
                        :type="INPUT_TYPE.TIME"
                        label="Start time" />
                    <InputComponent v-model="windowEnd" :type="INPUT_TYPE.TIME" label="End time" />
                    <span class="text-sm">
                        Translations run continuously between these times. When the window closes,
                        the translation in progress finishes and remaining ones wait for the next
                        window.
                    </span>

                    <span class="font-semibold">Translation service:</span>
                    <SelectComponent
                        :options="windowServiceOptions"
                        :selected="windowService"
                        placeholder="Default (use service order)"
                        @update:selected="(value: string) => (windowService = value)" />
                    <span class="text-sm">
                        Optionally use a different service for translations started by the window,
                        for example a cheaper local AI that can run overnight. Leave on default to
                        use the service order from the services page.
                    </span>
                </template>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useSettingStore } from '@/store/setting'
import { useRouter } from 'vue-router'
import { INPUT_TYPE, INPUT_VALIDATION_TYPE, IPluginSummary, SETTINGS } from '@/ts'
import servicesApi from '@/services'
import CardComponent from '@/components/common/CardComponent.vue'
import InputComponent from '@/components/common/InputComponent.vue'
import SelectComponent from '@/components/common/SelectComponent.vue'
import ToggleButton from '@/components/common/ToggleButton.vue'
import SaveNotification from '@/components/common/SaveNotification.vue'

const saveNotification = ref<InstanceType<typeof SaveNotification> | null>(null)
const maxTranslationsPerRunIsValid = ref(false)
const movieAgeThresholdIsValid = ref(false)
const showAgeThresholdIsValid = ref(false)
const movieScheduleIsValid = ref(false)
const showScheduleIsValid = ref(false)
const translationScheduleIsValid = ref(false)
const settingsStore = useSettingStore()
const router = useRouter()

const automationEnabled = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.AUTOMATION_ENABLED) as string,
    set: (newValue: string): void => {
        if (newValue === 'true' && windowEnabled.value === 'true') {
            settingsStore.updateSetting(SETTINGS.AUTOMATION_WINDOW_ENABLED, 'false', true)
        }
        settingsStore.updateSetting(SETTINGS.AUTOMATION_ENABLED, newValue, true)
        saveNotification.value?.show()
    }
})
const windowEnabled = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.AUTOMATION_WINDOW_ENABLED) as string,
    set: (newValue: string): void => {
        if (newValue === 'true' && automationEnabled.value === 'true') {
            settingsStore.updateSetting(SETTINGS.AUTOMATION_ENABLED, 'false', true)
        }
        settingsStore.updateSetting(SETTINGS.AUTOMATION_WINDOW_ENABLED, newValue, true)
        saveNotification.value?.show()
    }
})
const windowStart = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.AUTOMATION_WINDOW_START) as string,
    set: (newValue: string): void => {
        if (newValue) {
            settingsStore.updateSetting(SETTINGS.AUTOMATION_WINDOW_START, newValue, true)
            saveNotification.value?.show()
        }
    }
})
const windowEnd = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.AUTOMATION_WINDOW_END) as string,
    set: (newValue: string): void => {
        if (newValue) {
            settingsStore.updateSetting(SETTINGS.AUTOMATION_WINDOW_END, newValue, true)
            saveNotification.value?.show()
        }
    }
})
const windowService = computed({
    get: (): string =>
        (settingsStore.getSetting(SETTINGS.AUTOMATION_WINDOW_SERVICE_TYPE) as string) ?? '',
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.AUTOMATION_WINDOW_SERVICE_TYPE, newValue, true)
        saveNotification.value?.show()
    }
})
const windowTimezone = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.AUTOMATION_WINDOW_TIMEZONE) as string,
    set: (newValue: string): void => {
        if (newValue) {
            settingsStore.updateSetting(SETTINGS.AUTOMATION_WINDOW_TIMEZONE, newValue, true)
            saveNotification.value?.show()
        }
    }
})

const timezoneOptions = Intl.supportedValuesOf('timeZone').map((zone) => ({
    label: zone,
    value: zone
}))
const windowServiceOptions = ref<{ value: string; label: string }[]>([])
const now = ref(new Date())
let clockInterval: ReturnType<typeof setInterval> | undefined
onMounted(async () => {
    clockInterval = setInterval(() => (now.value = new Date()), 1000)
    const summaries: IPluginSummary[] = await servicesApi.plugin.list()
    windowServiceOptions.value = [
        { value: '', label: 'Default (use service order)' },
        ...summaries.map((summary) => ({ value: summary.provider, label: summary.displayName }))
    ]
})
onUnmounted(() => clearInterval(clockInterval))
const currentTimeInZone = computed((): string => {
    try {
        return now.value.toLocaleTimeString(undefined, {
            timeZone: windowTimezone.value,
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit'
        })
    } catch {
        return ''
    }
})
const movieSchedule = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MOVIE_SCHEDULE) as string,
    set: (newValue: string): void => {
        if (movieScheduleIsValid.value) {
            settingsStore.updateSetting(SETTINGS.MOVIE_SCHEDULE, newValue, true)
            saveNotification.value?.show()
        }
    }
})
const showSchedule = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SHOW_SCHEDULE) as string,
    set: (newValue: string): void => {
        if (showScheduleIsValid.value) {
            settingsStore.updateSetting(SETTINGS.SHOW_SCHEDULE, newValue, true)
            saveNotification.value?.show()
        }
    }
})
const translationSchedule = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.TRANSLATION_SCHEDULE) as string,
    set: (newValue: string): void => {
        if (translationScheduleIsValid.value) {
            settingsStore.updateSetting(SETTINGS.TRANSLATION_SCHEDULE, newValue, true)
            saveNotification.value?.show()
        }
    }
})
const maxTranslationsPerRun = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MAX_TRANSLATIONS_PER_RUN) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MAX_TRANSLATIONS_PER_RUN, newValue, true)
        saveNotification.value?.show()
    }
})

const movieAgeThreshold = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MOVIE_AGE_THRESHOLD) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MOVIE_AGE_THRESHOLD, newValue, true)
        saveNotification.value?.show()
    }
})

const showAgeThreshold = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SHOW_AGE_THRESHOLD) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.SHOW_AGE_THRESHOLD, newValue, true)
        saveNotification.value?.show()
    }
})
</script>
