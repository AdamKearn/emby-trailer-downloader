interface RemoteTrailer {
  Url: string;
}

interface ProviderIds {
  Tmdb?: string;
  Imdb?: string;
}

interface EmbyItem {
  Name: string;
  ServerId: string;
  Id: string;
  Path: string;
  RunTimeTicks?: number;
  RemoteTrailers: RemoteTrailer[];
  ProviderIds?: ProviderIds;
  IsFolder: boolean;
  Type: string;
  MediaType: string;
  ProductionYear: number;
}

export interface EmbyResponse {
  Items: EmbyItem[];
}