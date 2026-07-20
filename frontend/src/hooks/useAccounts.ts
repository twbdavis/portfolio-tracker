import { useQuery } from '@tanstack/react-query'
import { api } from '@/lib/api'
import type { Account } from '@/lib/types'

export function useAccounts() {
  return useQuery({
    queryKey: ['accounts'],
    queryFn: () => api.get<Account[]>('/api/accounts'),
  })
}
